export interface Clip {
  clipId: string;
  s3Key: string;
  timestamp: number;
  durationSeconds: number;
  date: string;
  keyframeCount: number;
  detectionType: string;
  detectionCount: number;
  createdAt: string;
  // Optional presigned URLs
  thumbnailUrl?: string;
  videoUrl?: string;
  keyframeUrls?: string[];
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

export interface PiDevice {
  thingName: string;
  online: boolean;
  version?: string;
  hostname?: string;
  lastHeartbeat?: string;
  updateStatus?: string;
  services?: Record<string, string>;
  updateAvailable?: boolean;
}

export interface SystemHealth {
  checkedAt: string;
  latestVersion?: string;
  devices: PiDevice[];
}
