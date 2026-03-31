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

  constructor(callback: ShadowUpdateCallback) {
    this.callback = callback;
  }

  async connect(): Promise<void> {
    const { presignedUrl, clientId } = await api.getIotCredentials();

    this.client = mqtt.connect(presignedUrl, {
      clientId,
      protocolVersion: 4,
      clean: true,
      reconnectPeriod: 0, // Don't auto-reconnect — we'll reconnect with fresh presigned URL
    });

    this.client.on("connect", () => {
      this.client?.subscribe("$aws/things/snoutspotter-+/shadow/update/documents");
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
      // URL expired or connection dropped — reconnect with fresh URL
      if (this.refreshTimer === null) return; // Already disconnecting
      this.scheduleReconnect(5000);
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
    try {
      await this.connect();
    } catch (e) {
      console.warn("IoT reconnect failed, retrying in 30s:", e);
      this.scheduleReconnect(30000);
    }
  }

  disconnect(): void {
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
