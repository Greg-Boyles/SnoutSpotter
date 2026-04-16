import { useEffect, useState } from "react";
import { Link, useNavigate, useLocation } from "react-router-dom";
import { ArrowLeft, Loader2, Play, HelpCircle, Database } from "lucide-react";
import { format } from "date-fns";
import { api } from "../api";

function Tooltip({ text }: { text: string }) {
  return (
    <span className="relative group inline-flex items-center ml-1">
      <HelpCircle className="w-3.5 h-3.5 text-gray-400 hover:text-gray-600 cursor-help" />
      <span className="absolute left-1/2 -translate-x-1/2 bottom-full mb-2 w-56 px-3 py-2 text-xs text-white bg-gray-800 rounded-lg shadow-lg opacity-0 group-hover:opacity-100 pointer-events-none transition-opacity duration-150 z-10 leading-relaxed">
        {text}
        <span className="absolute left-1/2 -translate-x-1/2 top-full border-4 border-transparent border-t-gray-800" />
      </span>
    </span>
  );
}

function FieldLabel({ children, tooltip }: { children: React.ReactNode; tooltip: string }) {
  return (
    <label className="flex items-center text-sm font-medium text-gray-700 mb-1">
      {children}
      <Tooltip text={tooltip} />
    </label>
  );
}

type ExportItem = Record<string, string>;

export default function SubmitTraining() {
  const navigate = useNavigate();
  const location = useLocation();
  const prefill = location.state as { exportId?: string; config?: Record<string, unknown> } | null;

  const [exports, setExports] = useState<ExportItem[]>([]);
  const [loading, setLoading] = useState(true);
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const [jobType, setJobType] = useState<"detector" | "classifier">(
    (prefill?.config?.jobType as "detector" | "classifier") || "detector"
  );
  const [selectedExport, setSelectedExport] = useState("");
  const [epochs, setEpochs] = useState(prefill?.config?.epochs ? Number(prefill.config.epochs) : 100);
  const [batchSize, setBatchSize] = useState(prefill?.config?.batchSize ? Number(prefill.config.batchSize) : 64);
  const [imageSize, setImageSize] = useState(prefill?.config?.imageSize ? Number(prefill.config.imageSize) : 640);
  const [learningRate, setLearningRate] = useState(prefill?.config?.learningRate ? Number(prefill.config.learningRate) : 0.01);
  const [workers, setWorkers] = useState(prefill?.config?.workers ? Number(prefill.config.workers) : 8);
  const [modelBase, setModelBase] = useState(prefill?.config?.modelBase ? String(prefill.config.modelBase) : "yolov8n.pt");
  const [notes, setNotes] = useState("");

  const handleJobTypeChange = (type: "detector" | "classifier") => {
    setJobType(type);
    if (type === "classifier") {
      setEpochs(50);
      setBatchSize(32);
      setImageSize(224);
      setLearningRate(0.001);
      setModelBase("mobilenet_v3_small");
    } else {
      setEpochs(100);
      setBatchSize(64);
      setImageSize(640);
      setLearningRate(0.01);
      setModelBase("yolov8n.pt");
    }
  };

  const filteredExports = exports.filter((e) => {
    let format = e.export_type || e.format || "detection";
    if (!e.export_type && !e.format && e.config) {
      try { format = JSON.parse(e.config).export_type || "detection"; } catch { /* ignore */ }
    }
    return jobType === "classifier" ? format === "classification" : format !== "classification";
  });

  useEffect(() => {
    api.listExports()
      .then((data) => {
        const complete = data.exports.filter((e) => e.status === "complete");
        setExports(complete);
        if (prefill?.exportId && complete.some((e) => e.export_id === prefill.exportId)) {
          setSelectedExport(prefill.exportId);
        } else if (complete.length > 0) {
          setSelectedExport(complete[0].export_id);
        }
      })
      .catch((e: Error) => setError(e.message))
      .finally(() => setLoading(false));
  }, []);

  useEffect(() => {
    if (filteredExports.length > 0 && !filteredExports.some((e) => e.export_id === selectedExport)) {
      setSelectedExport(filteredExports[0].export_id);
    } else if (filteredExports.length === 0) {
      setSelectedExport("");
    }
  }, [jobType, exports]);

  const handleSubmit = async () => {
    const exp = exports.find((e) => e.export_id === selectedExport);
    if (!exp) return;

    setSubmitting(true);
    setError(null);
    try {
      const result = await api.submitTrainingJob({
        exportId: exp.export_id,
        exportS3Key: exp.s3_key,
        epochs,
        batchSize,
        imageSize,
        learningRate,
        workers,
        modelBase,
        notes: notes || undefined,
        jobType,
      });
      navigate(`/training/${result.jobId}`);
    } catch (e) {
      setError((e as Error).message);
      setSubmitting(false);
    }
  };

  const selectedExp = filteredExports.find((e) => e.export_id === selectedExport);

  if (loading) {
    return (
      <div className="flex items-center gap-2 text-gray-400">
        <Loader2 className="w-4 h-4 animate-spin" /> Loading exports...
      </div>
    );
  }

  return (
    <div className="max-w-xl">
      <Link to="/training" className="inline-flex items-center gap-1 text-sm text-gray-500 hover:text-gray-700 mb-4">
        <ArrowLeft className="w-4 h-4" /> Training Jobs
      </Link>

      <h1 className="text-2xl font-bold text-gray-900 mb-6">New Training Job</h1>

      {/* Job type toggle */}
      <div className="mb-4 flex gap-2">
        <button
          onClick={() => handleJobTypeChange("detector")}
          className={`px-4 py-2 text-sm font-medium rounded-lg border transition-colors ${
            jobType === "detector"
              ? "bg-amber-50 border-amber-300 text-amber-800"
              : "bg-white border-gray-200 text-gray-500 hover:border-gray-300"
          }`}
        >
          Dog Detector (YOLO)
        </button>
        <button
          onClick={() => handleJobTypeChange("classifier")}
          className={`px-4 py-2 text-sm font-medium rounded-lg border transition-colors ${
            jobType === "classifier"
              ? "bg-purple-50 border-purple-300 text-purple-800"
              : "bg-white border-gray-200 text-gray-500 hover:border-gray-300"
          }`}
        >
          Dog Classifier (MobileNet)
        </button>
      </div>
      <p className="text-xs text-gray-500 mb-4">
        {jobType === "detector"
          ? "Trains a YOLO model to detect dogs in images. Uses detection-format exports."
          : "Trains a MobileNetV3 classifier to identify your pets vs other dogs on cropped patches. Uses classification-format exports."}
      </p>

      {error && (
        <div className="mb-4 p-3 bg-red-50 text-red-700 rounded-lg text-sm">{error}</div>
      )}

      {prefill && (
        <div className="mb-4 p-3 bg-blue-50 text-blue-700 rounded-lg text-sm">
          Pre-filled from a previous job. Review settings before submitting.
        </div>
      )}

      <div className="bg-white rounded-xl border border-gray-200 p-5 space-y-5">
        {/* Dataset */}
        <div>
          <FieldLabel tooltip="The labelled image export to train on. Only completed exports are shown. Larger datasets generally produce better models.">Dataset</FieldLabel>
          {filteredExports.length === 0 ? (
            <p className="text-sm text-gray-400">
              No {jobType === "classifier" ? "classification" : "detection"} exports available.{" "}
              <Link to="/exports" className="text-blue-600 hover:text-blue-700">Create an export</Link> first.
            </p>
          ) : (
            <>
              <select
                value={selectedExport}
                onChange={(e) => setSelectedExport(e.target.value)}
                className="w-full px-3 py-2 text-sm border border-gray-300 rounded-lg"
              >
                {filteredExports.map((exp) => (
                  <option key={exp.export_id} value={exp.export_id}>
                    {exp.export_id.slice(0, 8)}… — {exp.total_images} images, {exp.size_mb} MB
                  </option>
                ))}
              </select>

              {/* Dataset summary card */}
              {selectedExp && (
                <div className="mt-2 flex items-start gap-3 p-3 bg-gray-50 rounded-lg border border-gray-100">
                  <Database className="w-4 h-4 text-gray-400 mt-0.5 shrink-0" />
                  <div className="flex-1 min-w-0">
                    <div className="flex flex-wrap gap-x-4 gap-y-1 text-xs text-gray-600">
                      {selectedExp.total_images && (
                        <span><span className="font-medium">{selectedExp.total_images}</span> images total</span>
                      )}
                      {/* Per-pet counts from new exports */}
                      {selectedExp.pet_counts && (() => {
                        try {
                          const pc = typeof selectedExp.pet_counts === "string" ? JSON.parse(selectedExp.pet_counts) : selectedExp.pet_counts;
                          return Object.entries(pc as Record<string, number>).map(([petId, count]) => (
                            <span key={petId}><span className="font-medium">{String(count)}</span> {petId}</span>
                          ));
                        } catch { return null; }
                      })()}
                      {/* Legacy my_dog_count for old exports */}
                      {!selectedExp.pet_counts && selectedExp.my_dog_count && (
                        <span><span className="font-medium">{selectedExp.my_dog_count}</span> my_dog</span>
                      )}
                      {selectedExp.other_dog_count && (
                        <span><span className="font-medium">{selectedExp.other_dog_count}</span> other_dog</span>
                      )}
                      {selectedExp.size_mb && (
                        <span><span className="font-medium">{selectedExp.size_mb}</span> MB</span>
                      )}
                    </div>
                    {selectedExp.created_at && (
                      <p className="text-xs text-gray-400 mt-1">
                        Created {format(new Date(selectedExp.created_at), "d MMM yyyy, HH:mm")}
                      </p>
                    )}
                  </div>
                </div>
              )}
            </>
          )}
        </div>

        {/* Config grid */}
        <div className="grid grid-cols-2 gap-4">
          <div>
            <FieldLabel tooltip="Number of full passes through the dataset. More epochs can improve accuracy but increase training time. Early stopping kicks in after 20 epochs with no improvement.">Epochs</FieldLabel>
            <input
              type="number"
              min={10}
              max={500}
              value={epochs}
              onChange={(e) => setEpochs(Number(e.target.value))}
              className="w-full px-3 py-2 text-sm border border-gray-300 rounded-lg"
            />
          </div>
          <div>
            <FieldLabel tooltip="Number of images processed together per training step. Larger batches keep the GPU busier and train faster. For a 16 GB GPU (e.g. RTX 4080 Super) 64 is a safe maximum — 128 can exhaust system RAM due to DataLoader pre-fetching and will crash. Drop to 32 or 16 if you get CUDA out-of-memory errors.">Batch size</FieldLabel>
            <select
              value={batchSize}
              onChange={(e) => setBatchSize(Number(e.target.value))}
              className="w-full px-3 py-2 text-sm border border-gray-300 rounded-lg"
            >
              {[8, 16, 32, 64].map((v) => <option key={v} value={v}>{v}</option>)}
            </select>
          </div>
          <div>
            <FieldLabel tooltip={jobType === "classifier"
              ? "Classifier input resolution. 224px is the MobileNet default. Higher values may improve accuracy but increase training time and memory."
              : "Images are resized to this square resolution before training. 640px is the YOLO default and matches what RunInference uses at inference time — only change this if you also update the Lambda."
            }>Image size</FieldLabel>
            <select
              value={imageSize}
              onChange={(e) => setImageSize(Number(e.target.value))}
              className="w-full px-3 py-2 text-sm border border-gray-300 rounded-lg"
            >
              {(jobType === "classifier" ? [128, 160, 192, 224, 256] : [416, 640, 800]).map((v) => <option key={v} value={v}>{v}px</option>)}
            </select>
          </div>
          <div>
            <FieldLabel tooltip="Controls how much the model weights are adjusted per step. 0.01 is the YOLO default and works well for fine-tuning. Lower values (0.001) train more slowly but can be more stable with small datasets.">Learning rate</FieldLabel>
            <input
              type="number"
              step={0.001}
              min={0.0001}
              max={0.1}
              value={learningRate}
              onChange={(e) => setLearningRate(Number(e.target.value))}
              className="w-full px-3 py-2 text-sm border border-gray-300 rounded-lg"
            />
          </div>
          <div>
            <FieldLabel tooltip="Number of CPU threads used to load and pre-process images in parallel while the GPU trains. Set to the number of CPU cores available, but no more than 8. Has no effect on model quality.">Workers</FieldLabel>
            <input
              type="number"
              min={1}
              max={16}
              value={workers}
              onChange={(e) => setWorkers(Number(e.target.value))}
              className="w-full px-3 py-2 text-sm border border-gray-300 rounded-lg"
            />
          </div>
          <div>
            <FieldLabel tooltip={jobType === "classifier"
              ? "The pre-trained classifier backbone. MobileNetV3-Small is lightweight and fast — ideal for inference on cropped patches."
              : "The pre-trained YOLO checkpoint to fine-tune from. Nano (yolov8n) is faster to train and deploy — recommended. Small (yolov8s) is more accurate but larger and slower at inference."
            }>Base model</FieldLabel>
            <select
              value={modelBase}
              onChange={(e) => setModelBase(e.target.value)}
              className="w-full px-3 py-2 text-sm border border-gray-300 rounded-lg"
            >
              {jobType === "classifier" ? (
                <option value="mobilenet_v3_small">MobileNetV3-Small</option>
              ) : (
                <>
                  <option value="yolov8n.pt">YOLOv8n (nano)</option>
                  <option value="yolov8s.pt">YOLOv8s (small)</option>
                </>
              )}
            </select>
          </div>
        </div>

        {/* Notes */}
        <div>
          <FieldLabel tooltip="Optional description saved with the job — useful for tracking what changed between runs, e.g. 'Added 50 new bowl labels' or 'Increased epochs to 150'.">Notes (optional)</FieldLabel>
          <input
            type="text"
            value={notes}
            onChange={(e) => setNotes(e.target.value)}
            placeholder="e.g. Training with new bowl labels"
            className="w-full px-3 py-2 text-sm border border-gray-300 rounded-lg"
          />
        </div>

        {/* Submit */}
        <button
          onClick={handleSubmit}
          disabled={submitting || filteredExports.length === 0}
          className="w-full inline-flex items-center justify-center gap-2 px-4 py-2.5 text-sm font-medium text-white bg-blue-600 hover:bg-blue-700 rounded-lg disabled:opacity-50"
        >
          {submitting ? <Loader2 className="w-4 h-4 animate-spin" /> : <Play className="w-4 h-4" />}
          {submitting ? "Submitting..." : "Start Training"}
        </button>
      </div>
    </div>
  );
}
