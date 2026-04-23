using System.Net.Http.Headers;
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.Lambda.Core;
using Amazon.Lambda.SQSEvents;
using Amazon.Lambda.Serialization.SystemTextJson;
using Amazon.SecretsManager;
using Amazon.SQS;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SnoutSpotter.Lambda.SpcPoller.Models;
using SnoutSpotter.Lambda.SpcPoller.Services;
using SnoutSpotter.Spc.Client.Services;
using SnoutSpotter.Spc.Client.Services.Interfaces;

[assembly: LambdaSerializer(typeof(DefaultLambdaJsonSerializer))]

namespace SnoutSpotter.Lambda.SpcPoller;

// Motion-triggered burst poller for Sure Pet Care timeline events. See
// docs/plan-spc-event-ingestion.md for the full flow.
public class Function
{
    private static readonly TimeSpan BurstWindow = TimeSpan.FromMinutes(10);
    private const int TimelinePageSize = 50;

    private readonly BurstStateStore _burstState;
    private readonly HouseholdIntegrationReader _households;
    private readonly PetLinkReader _petLinks;
    private readonly EventStore _events;
    private readonly ISpcSecretsStore _secrets;
    private readonly ISpcApiClient _spc;
    private readonly BurstQueueProducer _continueProducer;

    public Function()
    {
        var dynamo = new AmazonDynamoDBClient();
        var sqs = new AmazonSQSClient();
        var sm = new AmazonSecretsManagerClient();

        var householdsTable = Env("HOUSEHOLDS_TABLE");
        var petsTable = Env("PETS_TABLE");
        var eventsTable = Env("SPC_EVENTS_TABLE");
        var burstStateTable = Env("SPC_BURST_STATE_TABLE");
        var queueUrl = Env("SPC_BURST_QUEUE_URL");
        var spcBaseUrl = Environment.GetEnvironmentVariable("SPC_BASE_URL") ?? "https://app-api.beta.surehub.io";

        _burstState = new BurstStateStore(dynamo, burstStateTable, BurstWindow);
        _households = new HouseholdIntegrationReader(dynamo, householdsTable);
        _petLinks = new PetLinkReader(dynamo, petsTable);
        _events = new EventStore(dynamo, eventsTable);
        _secrets = new SpcSecretsStore(sm, NullLogger<SpcSecretsStore>.Instance);

        var http = new HttpClient
        {
            BaseAddress = new Uri(spcBaseUrl),
            Timeout = TimeSpan.FromSeconds(10)
        };
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _spc = new SpcApiClient(http, NullLogger<SpcApiClient>.Instance);

        _continueProducer = new BurstQueueProducer(sqs, queueUrl);
    }

    private static string Env(string key) =>
        Environment.GetEnvironmentVariable(key)
        ?? throw new InvalidOperationException($"Env var {key} is required");

    public async Task Handle(SQSEvent evt, ILambdaContext context)
    {
        foreach (var record in evt.Records)
        {
            await HandleOne(record, context);
        }
    }

    private async Task HandleOne(SQSEvent.SQSMessage record, ILambdaContext ctx)
    {
        BurstMessage? msg;
        try
        {
            msg = JsonSerializer.Deserialize<BurstMessage>(record.Body);
        }
        catch (Exception ex)
        {
            ctx.Logger.LogError($"Malformed burst message body: {ex.Message}");
            return; // drop — no point redelivering a malformed message
        }
        if (msg == null || string.IsNullOrEmpty(msg.HouseholdId) || string.IsNullOrEmpty(msg.Kind))
        {
            ctx.Logger.LogError("Burst message missing householdId or kind");
            return;
        }

        // Kind=motion: start or extend the burst window. We always do this first
        // so subsequent motion events extend rather than reset.
        if (msg.Kind == BurstMessageKinds.Motion)
        {
            await _burstState.StartOrExtendAsync(msg.HouseholdId, CancellationToken.None);
        }

        var state = await _burstState.GetAsync(msg.HouseholdId, CancellationToken.None);
        if (state == null)
        {
            // Continue message for a household with no state — the row was likely
            // TTL'd. Nothing to do.
            return;
        }

        var now = DateTime.UtcNow;
        if (state.PollUntil <= now)
        {
            // Burst window has closed between enqueue and dequeue; let the chain die.
            return;
        }

        var (spcHouseholdIdStr, status) = await _households.GetAsync(msg.HouseholdId, CancellationToken.None);
        if (status != "linked" || string.IsNullOrEmpty(spcHouseholdIdStr))
        {
            // Unlinked mid-burst (or never linked). Nothing to poll.
            return;
        }
        if (!long.TryParse(spcHouseholdIdStr, out var spcHouseholdId))
        {
            ctx.Logger.LogError($"spc_household_id {spcHouseholdIdStr} not numeric for {msg.HouseholdId}");
            return;
        }

        var secret = await _secrets.GetAsync(msg.HouseholdId, CancellationToken.None);
        if (secret == null)
        {
            ctx.Logger.LogWarning($"No SPC secret for {msg.HouseholdId} despite linked status");
            return;
        }

        // Seed-on-first-motion: pull the newest id and store it without persisting
        // history. Saves us from backfilling weeks of events on first link.
        if (state.LastTimelineId == null)
        {
            try
            {
                var latest = await _spc.ListTimelineAsync(secret.AccessToken, spcHouseholdId, sinceId: null, pageSize: 1);
                if (latest.Count > 0)
                {
                    await _burstState.SetCursorAsync(msg.HouseholdId, latest.Max(e => e.Id), CancellationToken.None);
                }
                await _burstState.SetLastPollAtAsync(msg.HouseholdId, now, CancellationToken.None);
            }
            catch (SpcUnauthorizedException)
            {
                await _households.MarkTokenExpiredAsync(msg.HouseholdId, CancellationToken.None);
                return; // chain dies
            }
            catch (SpcUpstreamException ex)
            {
                ctx.Logger.LogWarning($"SPC upstream failure seeding {msg.HouseholdId}: {ex.Message}");
                throw; // let SQS redeliver
            }

            await _continueProducer.MaybeScheduleContinueAsync(msg.HouseholdId, state.PollUntil, CancellationToken.None);
            return;
        }

        // Normal poll
        List<Spc.Client.Models.SpcTimelineResource> page;
        try
        {
            page = await _spc.ListTimelineAsync(secret.AccessToken, spcHouseholdId, sinceId: state.LastTimelineId, pageSize: TimelinePageSize);
        }
        catch (SpcUnauthorizedException)
        {
            await _households.MarkTokenExpiredAsync(msg.HouseholdId, CancellationToken.None);
            return; // chain dies
        }
        catch (SpcUpstreamException ex)
        {
            ctx.Logger.LogWarning($"SPC upstream failure polling {msg.HouseholdId}: {ex.Message}");
            throw; // let SQS redeliver
        }

        if (page.Count > 0)
        {
            var petMap = await _petLinks.GetSpcToInternalMapAsync(msg.HouseholdId, CancellationToken.None);
            foreach (var ev in page)
            {
                try
                {
                    await _events.WriteAsync(msg.HouseholdId, ev, petMap, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    ctx.Logger.LogError($"Failed to persist event {ev.Id}: {ex.Message}");
                    // Keep going — losing one event is better than failing the whole batch
                }
            }
            var maxId = page.Max(e => e.Id);
            await _burstState.SetCursorAsync(msg.HouseholdId, maxId, CancellationToken.None);
        }

        await _burstState.SetLastPollAtAsync(msg.HouseholdId, now, CancellationToken.None);
        await _households.SetLastSyncAtAsync(msg.HouseholdId, now, CancellationToken.None);

        await _continueProducer.MaybeScheduleContinueAsync(msg.HouseholdId, state.PollUntil, CancellationToken.None);
    }
}
