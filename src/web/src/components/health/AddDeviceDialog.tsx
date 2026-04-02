import { useState } from "react";
import { X, Copy, Check } from "lucide-react";
import { api } from "../../api";

interface AddDeviceDialogProps {
  open: boolean;
  onClose: () => void;
  onDeviceAdded: () => void;
  onError: (message: string) => void;
}

export default function AddDeviceDialog({ open, onClose, onDeviceAdded, onError }: AddDeviceDialogProps) {
  const [newDeviceName, setNewDeviceName] = useState("");
  const [registrationResult, setRegistrationResult] = useState<any>(null);
  const [registering, setRegistering] = useState(false);
  const [copied, setCopied] = useState<string | null>(null);

  const handleAdd = async () => {
    if (!newDeviceName.trim()) return;
    setRegistering(true);
    try {
      const result = await api.registerDevice(newDeviceName.trim());
      setRegistrationResult(result);
      onDeviceAdded();
    } catch (e) {
      onError(`Registration failed: ${(e as Error).message}`);
      handleClose();
    } finally {
      setRegistering(false);
    }
  };

  const handleClose = () => {
    setNewDeviceName("");
    setRegistrationResult(null);
    onClose();
  };

  const copyToClipboard = (text: string, key: string) => {
    navigator.clipboard.writeText(text);
    setCopied(key);
    setTimeout(() => setCopied(null), 2000);
  };

  if (!open) return null;

  return (
    <div className="fixed inset-0 bg-black bg-opacity-50 flex items-center justify-center z-50">
      <div className="bg-white rounded-lg p-6 max-w-2xl w-full mx-4 max-h-[90vh] overflow-y-auto">
        {!registrationResult ? (
          <>
            <div className="flex items-center justify-between mb-4">
              <h3 className="text-lg font-semibold text-gray-900">Add Raspberry Pi Device</h3>
              <button onClick={handleClose} className="text-gray-400 hover:text-gray-600">
                <X className="w-5 h-5" />
              </button>
            </div>
            <p className="text-sm text-gray-600 mb-4">
              Enter a name for your new Pi device (e.g., "garage", "front-door").
            </p>
            <input
              type="text"
              value={newDeviceName}
              onChange={(e) => setNewDeviceName(e.target.value)}
              placeholder="Device name"
              className="w-full px-3 py-2 border border-gray-300 rounded-lg mb-4 focus:outline-none focus:ring-2 focus:ring-blue-500"
              disabled={registering}
            />
            <div className="flex items-center gap-3">
              <button
                onClick={handleAdd}
                disabled={registering || !newDeviceName.trim()}
                className="flex-1 px-4 py-2 bg-blue-600 text-white rounded-lg hover:bg-blue-700 disabled:opacity-50 disabled:cursor-not-allowed"
              >
                {registering ? "Registering..." : "Register Device"}
              </button>
              <button
                onClick={handleClose}
                disabled={registering}
                className="px-4 py-2 border border-gray-300 rounded-lg hover:bg-gray-50 disabled:opacity-50"
              >
                Cancel
              </button>
            </div>
          </>
        ) : (
          <>
            <div className="flex items-center justify-between mb-4">
              <h3 className="text-lg font-semibold text-green-600">Device Registered!</h3>
              <button onClick={handleClose} className="text-gray-400 hover:text-gray-600">
                <X className="w-5 h-5" />
              </button>
            </div>
            <p className="text-sm text-gray-600 mb-4">
              Save these credentials securely. You'll need them to set up the Pi.
            </p>
            <div className="space-y-3">
              {[
                { label: "Thing Name", value: registrationResult.thingName, key: "thingName" },
                { label: "IoT Endpoint", value: registrationResult.ioTEndpoint, key: "endpoint" },
              ].map(({ label, value, key }) => (
                <div key={key}>
                  <label className="text-xs font-medium text-gray-500">{label}:</label>
                  <div className="flex items-center gap-2 mt-1">
                    <code className="flex-1 p-2 bg-gray-100 rounded text-xs break-all">{value}</code>
                    <button onClick={() => copyToClipboard(value, key)} className="p-2 hover:bg-gray-100 rounded">
                      {copied === key ? <Check className="w-4 h-4 text-green-600" /> : <Copy className="w-4 h-4" />}
                    </button>
                  </div>
                </div>
              ))}
              {[
                { label: "Certificate", value: registrationResult.certificatePem, key: "cert" },
                { label: "Private Key", value: registrationResult.privateKey, key: "key" },
              ].map(({ label, value, key }) => (
                <div key={key}>
                  <label className="text-xs font-medium text-gray-500">{label}:</label>
                  <div className="flex items-center gap-2 mt-1">
                    <textarea readOnly value={value} className="flex-1 p-2 bg-gray-100 rounded text-xs font-mono h-20 resize-none" />
                    <button onClick={() => copyToClipboard(value, key)} className="p-2 hover:bg-gray-100 rounded">
                      {copied === key ? <Check className="w-4 h-4 text-green-600" /> : <Copy className="w-4 h-4" />}
                    </button>
                  </div>
                </div>
              ))}
            </div>
            <button
              onClick={handleClose}
              className="mt-4 w-full px-4 py-2 bg-blue-600 text-white rounded-lg hover:bg-blue-700"
            >
              Done
            </button>
          </>
        )}
      </div>
    </div>
  );
}
