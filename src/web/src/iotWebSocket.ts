/**
 * Sign an IoT Core WebSocket URL with SigV4 and manage MQTT subscriptions
 * for real-time device shadow updates.
 */

import mqtt from "mqtt";
import { api } from "./api";

interface IotCredentials {
  accessKeyId: string;
  secretAccessKey: string;
  sessionToken: string;
  expiration: string;
  iotEndpoint: string;
  region: string;
}

async function hmac(key: BufferSource, data: string): Promise<ArrayBuffer> {
  const k = await crypto.subtle.importKey("raw", key, { name: "HMAC", hash: "SHA-256" }, false, ["sign"]);
  return crypto.subtle.sign("HMAC", k, new TextEncoder().encode(data));
}

function hex(buf: ArrayBuffer): string {
  return Array.from(new Uint8Array(buf))
    .map((b) => b.toString(16).padStart(2, "0"))
    .join("");
}

async function sha256(data: string): Promise<string> {
  const buf = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(data));
  return hex(buf);
}

async function getSignatureKey(
  key: string, datestamp: string, region: string, service: string
): Promise<ArrayBuffer> {
  let k = await hmac(new TextEncoder().encode("AWS4" + key), datestamp);
  k = await hmac(k, region);
  k = await hmac(k, service);
  k = await hmac(k, "aws4_request");
  return k;
}

function rfc3986Encode(str: string): string {
  return encodeURIComponent(str).replace(/[!'()*]/g, (c) => `%${c.charCodeAt(0).toString(16).toUpperCase()}`);
}

async function createPresignedUrl(creds: IotCredentials): Promise<string> {
  const host = creds.iotEndpoint;
  const region = creds.region;
  const now = new Date();
  const datestamp = now.toISOString().replace(/[-:]/g, "").slice(0, 8);
  const amzDate = now.toISOString().replace(/[-:]/g, "").replace(/\.\d+/, "");
  const credentialScope = `${datestamp}/${region}/iotdevicegateway/aws4_request`;

  // Build sorted query parameters with RFC 3986 encoding (required by SigV4)
  const queryParams: Record<string, string> = {
    "X-Amz-Algorithm": "AWS4-HMAC-SHA256",
    "X-Amz-Credential": `${creds.accessKeyId}/${credentialScope}`,
    "X-Amz-Date": amzDate,
    "X-Amz-Expires": "86400",
    "X-Amz-SignedHeaders": "host",
  };
  if (creds.sessionToken) {
    queryParams["X-Amz-Security-Token"] = creds.sessionToken;
  }

  const sortedKeys = Object.keys(queryParams).sort();
  const canonicalQueryString = sortedKeys
    .map((k) => `${rfc3986Encode(k)}=${rfc3986Encode(queryParams[k])}`)
    .join("&");

  const canonicalRequest = `GET\n/mqtt\n${canonicalQueryString}\nhost:${host}\n\nhost\n${await sha256("")}`;
  const stringToSign = `AWS4-HMAC-SHA256\n${amzDate}\n${credentialScope}\n${await sha256(canonicalRequest)}`;

  const signingKey = await getSignatureKey(creds.secretAccessKey, datestamp, region, "iotdevicegateway");
  const signatureBuf = await hmac(signingKey, stringToSign);
  const signature = hex(signatureBuf);

  return `wss://${host}/mqtt?${canonicalQueryString}&X-Amz-Signature=${signature}`;
}

export type ShadowUpdateCallback = (thingName: string, reported: Record<string, unknown>) => void;

export class IotConnection {
  private client: mqtt.MqttClient | null = null;
  private creds: IotCredentials | null = null;
  private callback: ShadowUpdateCallback;
  private refreshTimer: ReturnType<typeof setTimeout> | null = null;

  constructor(callback: ShadowUpdateCallback) {
    this.callback = callback;
  }

  async connect(): Promise<void> {
    this.creds = await api.getIotCredentials();
    const url = await createPresignedUrl(this.creds);

    const clientId = `browser-${Date.now()}-${Math.random().toString(36).slice(2, 8)}`;

    this.client = mqtt.connect(url, {
      clientId,
      protocolVersion: 4,
      clean: true,
      reconnectPeriod: 5000,
    });

    this.client.on("connect", () => {
      // Subscribe to shadow document updates for all snoutspotter devices
      this.client?.subscribe("$aws/things/snoutspotter-+/shadow/update/documents");
    });

    this.client.on("message", (_topic, payload) => {
      try {
        const msg = JSON.parse(payload.toString());
        const reported = msg?.current?.state?.reported;

        // Extract thing name from topic
        const topicMatch = _topic.match(/\$aws\/things\/(snoutspotter-[^/]+)\/shadow/);
        if (topicMatch && reported) {
          this.callback(topicMatch[1], reported);
        }
      } catch {
        // Ignore malformed messages
      }
    });

    // Refresh credentials before they expire (50 minutes)
    this.refreshTimer = setTimeout(() => this.reconnect(), 50 * 60 * 1000);
  }

  private async reconnect(): Promise<void> {
    this.disconnect();
    await this.connect();
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
