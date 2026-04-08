# Plan: SQS-Based Training Job Queue

## Context

Training jobs are currently dispatched via IoT shadow — the API writes the job to the first online agent's shadow desired state. This has three problems: (1) no check if the agent is already busy, (2) jobs submitted when no agents are online sit as `pending` forever, (3) no retry if an agent crashes mid-training.

Replace with an SQS queue that agents poll. Jobs are always queued regardless of agent availability. Agents compete to consume from the queue. Failed jobs retry automatically via visibility timeout, then go to a DLQ.

## Architecture

```
API submits job → DynamoDB (status=pending) + SQS message
                                                  ↓ agent polls
                                          Training Agent picks up message
                                                  ↓
                                          Runs training (progress via MQTT — unchanged)
                                                  ↓
                                          On success: deletes SQS message
                                          On failure: message returns to queue (retry)
                                          After N retries: → DLQ
```

Shadow is **no longer used for job dispatch** — only for heartbeat/status reporting and agent updates.

## Changes

### 1. CDK: SQS Queue

**`src/infra/Stacks/TrainingProgressStack.cs`**

Add alongside existing IoT Rule + Lambda:
- DLQ: `snout-spotter-training-jobs-dlq` (retention 7 days)
- Queue: `snout-spotter-training-jobs-queue` (standard, not FIFO)
  - VisibilityTimeout = 12 hours (training can run for hours; agent extends periodically)
  - RetentionPeriod = 3 days
  - MaxReceiveCount = 2 (retry once, then DLQ)
- SSM param: `/snoutspotter/training/job-queue-url`

Standard queue chosen over FIFO because training jobs are independent (order doesn't matter), multiple agents should consume concurrently, and at-least-once delivery is handled by idempotent self-assignment check.

### 2. Shared: SQS Message Contract

**`src/shared/SnoutSpotter.Contracts/TrainingJobMessage.cs`** (new)

Referenced by API (producer) and training agent (consumer).

### 3. API: Queue-Based Dispatch

**`src/api/Services/TrainingService.cs`** — rewrite `SubmitJobAsync`:

1. Write job to DynamoDB (status=pending) — unchanged
2. Send SQS message with `TrainingJobMessage` — replaces shadow dispatch
3. Remove agent discovery + shadow write logic
4. Remove `agent_thing_name` assignment at submit time (agent self-assigns when it picks up the job)

### 4. Training Agent: Poll SQS Instead of Shadow Delta

Remove shadow delta handling for `trainingJob`. Keep shadow for:
- Heartbeat / status reporting
- `cancelJob` commands
- `agentVersion` updates

New `SqsJobConsumer` class handles:
- Long-poll the queue (WaitTimeSeconds=20)
- Deserialize `TrainingJobMessage`
- Self-assign: update DynamoDB job with `agent_thing_name`
- On success: delete SQS message
- On failure: don't delete — message becomes visible again after timeout
- Periodically extend visibility timeout during long training runs (every 10 min)

### 5. Agent Shadow: Report Current Job

Agent reports `currentJobId` and `currentJobProgress` in shadow reported state so the UI can show which agent is working on which job.

### 6. Cancel Flow

Cancel still writes `cancelJob` to shadow desired state. Agent checks shadow for cancel signals during training, kills process, does NOT delete SQS message (returns to queue).

### 7. Frontend

Update agent cards on `TrainingJobs.tsx` to show current job badge from shadow with link to job detail page.

## Files to modify

| File | Change |
|------|--------|
| `src/infra/Stacks/TrainingProgressStack.cs` | Add SQS queue + DLQ + SSM param |
| `src/infra/Stacks/ApiStack.cs` | Read SSM param, add env var + SQS permission |
| `src/shared/SnoutSpotter.Contracts/TrainingJobMessage.cs` | **New** — SQS message contract |
| `src/api/Services/TrainingService.cs` | Replace shadow dispatch with SQS send |
| `src/api/AppConfig.cs` | Add `TrainingJobQueueUrl` |
| `src/api/Program.cs` | Bind env var |
| `src/training-agent/SnoutSpotter.TrainingAgent/SqsJobConsumer.cs` | **New** — SQS polling + visibility management |
| `src/training-agent/SnoutSpotter.TrainingAgent/Program.cs` | Replace shadow job handler with SQS poll loop, report currentJobId |
| `src/training-agent/SnoutSpotter.TrainingAgent/SnoutSpotter.TrainingAgent.csproj` | Add AWSSDK.SQS package |
| `src/shared/AgentReportedState.cs` | Add `CurrentJobId` and `CurrentJobProgress` fields |
| `src/web/src/pages/TrainingJobs.tsx` | Show current job badge on agent cards |

## Implementation order

1. CDK: SQS queue + SSM param + API wiring
2. Shared: TrainingJobMessage contract + AgentReportedState fields
3. API: rewrite SubmitJobAsync to send SQS
4. Training Agent: SqsJobConsumer + rewrite main loop
5. Frontend: agent card current job badge
6. Test end-to-end
