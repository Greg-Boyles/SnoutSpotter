import { useEffect, useState } from "react";
import { Link, useNavigate } from "react-router-dom";
import { ArrowLeft, Loader2, Play } from "lucide-react";
import { api } from "../api";

export default function SubmitTraining() {
  const navigate = useNavigate();
  const [exports, setExports] = useState<Record<string, string>[]>([]);
  const [loading, setLoading] = useState(true);
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const [selectedExport, setSelectedExport] = useState("");
  const [epochs, setEpochs] = useState(100);
  const [batchSize, setBatchSize] = useState(16);
  const [imageSize, setImageSize] = useState(640);
  const [learningRate, setLearningRate] = useState(0.01);
  const [workers, setWorkers] = useState(8);
  const [modelBase, setModelBase] = useState("yolov8n.pt");
  const [notes, setNotes] = useState("");

  useEffect(() => {
    api.listExports()
      .then((data) => {
        const complete = data.exports.filter((e) => e.status === "complete");
        setExports(complete);
        if (complete.length > 0) setSelectedExport(complete[0].export_id);
      })
      .catch((e: Error) => setError(e.message))
      .finally(() => setLoading(false));
  }, []);

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
      });
      navigate(`/training/${result.jobId}`);
    } catch (e) {
      setError((e as Error).message);
      setSubmitting(false);
    }
  };

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

      {error && (
        <div className="mb-4 p-3 bg-red-50 text-red-700 rounded-lg text-sm">{error}</div>
      )}

      <div className="bg-white rounded-xl border border-gray-200 p-5 space-y-5">
        {/* Dataset */}
        <div>
          <label className="block text-sm font-medium text-gray-700 mb-1">Dataset</label>
          {exports.length === 0 ? (
            <p className="text-sm text-gray-400">
              No exports available.{" "}
              <Link to="/exports" className="text-blue-600 hover:text-blue-700">Create an export</Link> first.
            </p>
          ) : (
            <select
              value={selectedExport}
              onChange={(e) => setSelectedExport(e.target.value)}
              className="w-full px-3 py-2 text-sm border border-gray-300 rounded-lg"
            >
              {exports.map((exp) => (
                <option key={exp.export_id} value={exp.export_id}>
                  {exp.export_id} — {exp.total_images} images, {exp.size_mb} MB
                </option>
              ))}
            </select>
          )}
        </div>

        {/* Config grid */}
        <div className="grid grid-cols-2 gap-4">
          <div>
            <label className="block text-sm font-medium text-gray-700 mb-1">Epochs</label>
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
            <label className="block text-sm font-medium text-gray-700 mb-1">Batch size</label>
            <select
              value={batchSize}
              onChange={(e) => setBatchSize(Number(e.target.value))}
              className="w-full px-3 py-2 text-sm border border-gray-300 rounded-lg"
            >
              {[8, 16, 32].map((v) => <option key={v} value={v}>{v}</option>)}
            </select>
          </div>
          <div>
            <label className="block text-sm font-medium text-gray-700 mb-1">Image size</label>
            <select
              value={imageSize}
              onChange={(e) => setImageSize(Number(e.target.value))}
              className="w-full px-3 py-2 text-sm border border-gray-300 rounded-lg"
            >
              {[416, 640, 800].map((v) => <option key={v} value={v}>{v}px</option>)}
            </select>
          </div>
          <div>
            <label className="block text-sm font-medium text-gray-700 mb-1">Learning rate</label>
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
            <label className="block text-sm font-medium text-gray-700 mb-1">Workers</label>
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
            <label className="block text-sm font-medium text-gray-700 mb-1">Base model</label>
            <select
              value={modelBase}
              onChange={(e) => setModelBase(e.target.value)}
              className="w-full px-3 py-2 text-sm border border-gray-300 rounded-lg"
            >
              <option value="yolov8n.pt">YOLOv8n (nano)</option>
              <option value="yolov8s.pt">YOLOv8s (small)</option>
            </select>
          </div>
        </div>

        {/* Notes */}
        <div>
          <label className="block text-sm font-medium text-gray-700 mb-1">Notes (optional)</label>
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
          disabled={submitting || exports.length === 0}
          className="w-full inline-flex items-center justify-center gap-2 px-4 py-2.5 text-sm font-medium text-white bg-blue-600 hover:bg-blue-700 rounded-lg disabled:opacity-50"
        >
          {submitting ? <Loader2 className="w-4 h-4 animate-spin" /> : <Play className="w-4 h-4" />}
          {submitting ? "Submitting..." : "Start Training"}
        </button>
      </div>
    </div>
  );
}
