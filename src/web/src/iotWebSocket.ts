/**
 * Real-time device shadow updates via IoT Core MQTT over WebSocket.
 * Signs the WebSocket URL client-side using credentials from the API.
 */

import mqtt from "mqtt";
import { api } from "./api";

// SigV4 signing helpers using Web Crypto API
async function hmac(key: BufferSource, data: string): Promise<ArrayBuffer> {
  const k = await crypto.subtle.importKey("raw", key, { name: "HMAC", hash: "SHA-256" }, false, ["sign"]);
  return crypto.subtle.sign("HMAC", k, new TextEncoder().encode(data));
}

function hex(buf: ArrayBuffer): string {
  return Array.from(new Uint8Array(buf)).map((b) => b.toString(16).padStart(2, "0")).join("");
}

async function sha256(data: string): Promise<string> {
  const buf = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(data));
  return hex(buf);
}

async function deriveSigningKey(secret: string, datestamp: string, region: string, service: string): Promise<ArrayBuffer> {
  let k = await hmac(new TextEncoder().encode("AWS4" + secret), datestamp);
  k = await hmac(k, region);
  k = await hmac(k, service);
  k = await hmac(k, "aws4_request");
  return k;
}

function awsEncode(str: string): string {
  return encodeURIComponent(str).replace(/[!'()*]/g, (c) => `%${c.charCodeAt(0).toString(16).toUpperCase()}`);
}

async function createPresignedUrl(
  host: string, accessKeyId: string, secretAccessKey: string, sessionToken: string, region: string
): Promise<string> {
  const now = new Date();
  const datestamp = now.toISOString().replace(/[-:]/g, "").slice(0, 8);
  const amzDate = now.toISOString().replace(/[-:]/g, "").replace(/\.\d+/, "");
  const service = "iotdevicegateway";
  const credentialScope = `${datestamp}/${region}/${service}/aws4_request`;

  const params: Record<string, string> = {
    "X-Amz-Algorithm": "AWS4-HMAC-SHA256",
    "X-Amz-Credential": `${accessKeyId}/${credentialScope}`,
    "X-Amz-Date": amzDate,
    "X-Amz-Expires": "86400",
    "X-Amz-SignedHeaders": "host",
  };
  if (sessionToken) {
    params["X-Amz-Security-Token"] = sessionToken;
  }

  const sortedKeys = Object.keys(params).sort();
  const canonicalQueryString = sortedKeys.map((k) => `${awsEncode(k)}=${awsEncode(params[k])}`).join("&");
  const canonicalHeaders = `host:${host}\n`;
  const payloadHash = await sha256("");

  const canonicalRequest = ["GET", "/mqtt", canonicalQueryString, canonicalHeaders, "host", payloadHash].join("\n");
  const stringToSign = ["AWS4-HMAC-SHA256", amzDate, credentialScope, await sha256(canonicalRequest)].join("\n");

  const signingKey = await deriveSigningKey(secretAccessKey, datestamp, region, service);
  const signature = hex(await hmac(signingKey, stringToSign));

  return `wss://${host}/mqtt?${canonicalQueryString}&X-Amz-Signature=${signature}`;
}

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
    const creds = await api.getIotCredentials();
    const presignedUrl = await createPresignedUrl(
      creds.iotEndpoint, creds.accessKeyId, creds.secretAccessKey, creds.sessionToken, creds.region
    );

    this.client = mqtt.connect({
      clientId: creds.clientId,
      protocolVersion: 4,
      clean: true,
      reconnectPeriod: 0,
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
