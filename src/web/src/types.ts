export interface Clip {
  clipId: string;
  s3Key: string;
  timestamp: number;
  durationSeconds: number;
  date: string;
  device?: string;
  keyframeCount: number;
  detectionType: string;
  detectionCount: number;
  createdAt: string;
  keyframeKeys?: string[];
  // Optional presigned URLs
  thumbnailUrl?: string;
  videoUrl?: string;
  keyframeUrls?: string[];
  keyframeDetections?: KeyframeDetection[];
}

export interface KeyframeDetection {
  keyframeKey: string;
  label: string;
  detections: DetectionBox[];
}

export interface DetectionBox {
  label: string;
  confidence: number;
  boundingBox: { x: number; y: number; width: number; height: number };
}

export interface Detection {
  detectionId: string;
  clipId: string;
  timestamp: string;
  confidence: number;
  label: string;
  boundingBox: { x: number; y: number; width: number; height: number };
  isTargetDog: boolean;
}

export interface StatsOverview {
  totalClips: number;
  clipsToday: number;
  totalDetections: number;
  myDogDetections: number;
  lastUploadTime: string | null;
  piOnline: boolean;
}

export interface CameraStatus {
  connected: boolean;
  healthy: boolean;
  sensor?: string;
  resolution?: string;
  recordResolution?: string;
}

export interface UploadStats {
  uploadsToday: number;
  failedToday: number;
  totalUploaded: number;
}

export interface SystemInfo {
  cpuTempC?: number;
  memUsedPercent?: number;
  diskUsedPercent?: number;
  diskFreeGb?: number;
  uptimeSeconds?: number;
  loadAvg?: number[];
  piModel?: string;
  ipAddress?: string;
  wifiSignalDbm?: number;
  wifiSsid?: string;
  pythonVersion?: string;
}

export interface PiDevice {
  thingName: string;
  online: boolean;
  version?: string;
  hostname?: string;
  lastHeartbeat?: string;
  deviceTime?: string;
  updateStatus?: string;
  services?: Record<string, string>;
  camera?: CameraStatus;
  lastMotionAt?: string;
  lastUploadAt?: string;
  uploadStats?: UploadStats;
  clipsPending?: number;
  system?: SystemInfo;
  config?: Record<string, number | boolean | string>;
  configErrors?: Record<string, string>;
  logShipping?: boolean;
  logShippingError?: string;
  streaming?: boolean;
  updateAvailable?: boolean;
}

export interface LogEntry {
  timestamp: string;
  level: string;
  service: string;
  message: string;
}

export interface StreamStartResult {
  streamName: string;
  region: string;
}

export interface SystemHealth {
  checkedAt: string;
  latestVersion?: string;
  devices: PiDevice[];
}
