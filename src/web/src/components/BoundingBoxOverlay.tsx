import type { Detection } from "../types";

interface Props {
  detection: Detection;
  imageWidth?: number;
  imageHeight?: number;
}

export default function BoundingBoxOverlay({
  detection,
  imageWidth = 1920,
  imageHeight = 1080,
}: Props) {
  const { boundingBox: bb, isTargetDog } = detection;
  const color = isTargetDog ? "#d97706" : "#6b7280";

  return (
    <svg
      viewBox={`0 0 ${imageWidth} ${imageHeight}`}
      className="absolute inset-0 w-full h-full"
      preserveAspectRatio="xMidYMid meet"
    >
      <rect
        x={bb.x}
        y={bb.y}
        width={bb.width}
        height={bb.height}
        fill="none"
        stroke={color}
        strokeWidth={3}
        rx={4}
      />
      <rect
        x={bb.x}
        y={bb.y - 20}
        width={bb.width}
        height={20}
        fill={color}
        rx={4}
      />
      <text
        x={bb.x + 4}
        y={bb.y - 5}
        fill="white"
        fontSize={12}
        fontFamily="system-ui"
      >
        {detection.label} {(detection.confidence * 100).toFixed(0)}%
      </text>
    </svg>
  );
}
