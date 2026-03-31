/**
 * Real-time device shadow updates via IoT Core MQTT over WebSocket.
 * Uses a presigned URL from the API — no client-side SigV4 signing needed.
 */

import mqtt from "mqtt";
import { api } from "./api";

export type ShadowUpdateCallback = (thingName: string, reported: Record<string, unknown>) => void;

export class IotConnection {
  private client: mqtt.MqttClient | null = null;
  private callback: ShadowUpdateCallback;
  private refreshTimer: ReturnType<typeof setTimeout> | null = null;
  private active = true;

  constructor(callback: ShadowUpdateCallback) {
    this.callback = callback;
  }

  async connect(): Promise<void> {
    const { presignedUrl, clientId } = await api.getIotCredentials();

    // mqtt.js needs a custom WebSocket factory to avoid modifying the presigned URL
    this.client = mqtt.connect({
      clientId,
      protocolVersion: 4,
      clean: true,
      reconnectPeriod: 0,
      browserBufferSize: 512 * 1024,
      createWebsocket: () => new WebSocket(presignedUrl, ["mqtt"]),
    } as mqtt.IClientOptions);

    this.client.on("connect", () => {
      console.log("IoT MQTT connected");
      this.client?.subscribe("$aws/things/snoutspotter-+/shadow/update/documents");
    });

    this.client.on("error", (err) => {
      console.warn("IoT MQTT error:", err.message);
    });

    this.client.on("message", (topic, payload) => {
      try {
        const msg = JSON.parse(payload.toString());
        const reported = msg?.current?.state?.reported;
        const topicMatch = topic.match(/\$aws\/things\/(snoutspotter-[^/]+)\/shadow/);
        if (topicMatch && reported) {
          this.callback(topicMatch[1], reported);
        }
      } catch {
        // Ignore malformed messages
      }
    });

    this.client.on("close", () => {
      if (!this.active) return;
      console.log("IoT MQTT closed, reconnecting in 10s...");
      this.scheduleReconnect(10000);
    });

    // Proactively reconnect before the URL/credentials expire (50 minutes)
    this.refreshTimer = setTimeout(() => this.reconnect(), 50 * 60 * 1000);
  }

  private scheduleReconnect(delayMs: number): void {
    if (this.refreshTimer) clearTimeout(this.refreshTimer);
    this.refreshTimer = setTimeout(() => this.reconnect(), delayMs);
  }

  private async reconnect(): Promise<void> {
    if (this.client) {
      this.client.end(true);
      this.client = null;
    }
    if (!this.active) return;
    try {
      await this.connect();
    } catch (e) {
      console.warn("IoT reconnect failed, retrying in 30s:", e);
      this.scheduleReconnect(30000);
    }
  }

  disconnect(): void {
    this.active = false;
    if (this.refreshTimer) {
      clearTimeout(this.refreshTimer);
      this.refreshTimer = null;
    }
    if (this.client) {
      this.client.end(true);
      this.client = null;
    }
  }
}
