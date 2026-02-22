export interface Clip {
  clipId: string;
  uploadedAt: string;
  durationSeconds: number;
  fileSizeBytes: number;
  status: "pending" | "processing" | "complete" | "error";
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
  totalDetections: number;
  targetDogDetections: number;
  clipsTodayCount: number;
  averageConfidence: number;
  storageUsedBytes: number;
}

export interface SystemHealth {
  piOnline: boolean;
  piLastSeen: string;
  cpuTemp: number;
  diskUsagePercent: number;
  uploadQueueSize: number;
  apiHealthy: boolean;
}
