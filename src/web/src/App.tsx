import { useEffect } from "react";
import { Routes, Route, NavLink } from "react-router-dom";
import { Dog, Video, Search, Activity, LayoutDashboard, LogOut, Radio, Tag, Cpu, Package, GraduationCap } from "lucide-react";
import { useOktaAuth, LoginCallback } from "@okta/okta-react";
import Dashboard from "./pages/Dashboard";
import ClipsBrowser from "./pages/ClipsBrowser";
import ClipDetail from "./pages/ClipDetail";
import Detections from "./pages/Detections";
import SystemHealthPage from "./pages/SystemHealth";
import DeviceConfig from "./pages/DeviceConfig";
import DeviceLogs from "./pages/DeviceLogs";
import LiveView from "./pages/LiveView";
import CommandHistory from "./pages/CommandHistory";
import DeviceDetail from "./pages/DeviceDetail";
import DeviceShadow from "./pages/DeviceShadow";
import Labels from "./pages/Labels";
import LabelDetail from "./pages/LabelDetail";
import TrainingExports from "./pages/TrainingExports";
import Models from "./pages/Models";
import TrainingJobs from "./pages/TrainingJobs";
import SubmitTraining from "./pages/SubmitTraining";
import TrainingJobDetail from "./pages/TrainingJobDetail";
import { setAuthGetter } from "./api";

const navItems = [
  { to: "/", icon: LayoutDashboard, label: "Dashboard" },
  { to: "/clips", icon: Video, label: "Clips" },
  { to: "/detections", icon: Search, label: "Detections" },
  { to: "/live", icon: Radio, label: "Live" },
  { to: "/labels", icon: Tag, label: "Labels" },
  { to: "/exports", icon: Package, label: "Exports" },
  { to: "/models", icon: Cpu, label: "Models" },
  { to: "/training", icon: GraduationCap, label: "Training" },
  { to: "/health", icon: Activity, label: "System" },
];

function RequiredAuth({ children }: { children: React.ReactNode }) {
  const { authState, oktaAuth } = useOktaAuth();

  useEffect(() => {
    if (authState && !authState.isAuthenticated) {
      oktaAuth.signInWithRedirect();
    }
  }, [authState, oktaAuth]);

  if (!authState || !authState.isAuthenticated) {
    return (
      <div className="flex items-center justify-center min-h-screen">
        <p className="text-gray-500">Loading...</p>
      </div>
    );
  }

  return <>{children}</>;
}

export default function App() {
  const { oktaAuth } = useOktaAuth();

  useEffect(() => {
    setAuthGetter(() => oktaAuth.getAccessToken());
  }, [oktaAuth]);

  return (
    <Routes>
      <Route path="/login/callback" element={<LoginCallback />} />
      <Route
        path="*"
        element={
          <RequiredAuth>
            <div className="flex min-h-screen bg-gray-50">
              <nav className="w-56 bg-white border-r border-gray-200 flex flex-col">
                <div className="flex items-center gap-2 px-4 py-5 border-b border-gray-200">
                  <Dog className="w-7 h-7 text-amber-600" />
                  <span className="text-lg font-bold text-gray-900">SnoutSpotter</span>
                </div>
                <ul className="flex-1 px-2 py-4 space-y-1">
                  {navItems.map(({ to, icon: Icon, label }) => (
                    <li key={to}>
                      <NavLink
                        to={to}
                        end={to === "/"}
                        className={({ isActive }) =>
                          `flex items-center gap-3 px-3 py-2 rounded-lg text-sm font-medium transition-colors ${
                            isActive
                              ? "bg-amber-50 text-amber-700"
                              : "text-gray-600 hover:bg-gray-100"
                          }`
                        }
                      >
                        <Icon className="w-5 h-5" />
                        {label}
                      </NavLink>
                    </li>
                  ))}
                </ul>
                <div className="px-2 py-4 border-t border-gray-200">
                  <button
                    onClick={() => oktaAuth.signOut()}
                    className="flex items-center gap-3 px-3 py-2 rounded-lg text-sm font-medium text-gray-600 hover:bg-gray-100 w-full"
                  >
                    <LogOut className="w-5 h-5" />
                    Sign out
                  </button>
                </div>
              </nav>

              <main className="flex-1 p-6 overflow-y-auto">
                <Routes>
                  <Route path="/" element={<Dashboard />} />
                  <Route path="/clips" element={<ClipsBrowser />} />
                  <Route path="/clips/:id" element={<ClipDetail />} />
                  <Route path="/detections" element={<Detections />} />
                  <Route path="/live" element={<LiveView />} />
                  <Route path="/labels/:keyframeKey" element={<LabelDetail />} />
                  <Route path="/labels" element={<Labels />} />
                  <Route path="/exports" element={<TrainingExports />} />
                  <Route path="/models" element={<Models />} />
                  <Route path="/training" element={<TrainingJobs />} />
                  <Route path="/training/new" element={<SubmitTraining />} />
                  <Route path="/training/:jobId" element={<TrainingJobDetail />} />
                  <Route path="/health" element={<SystemHealthPage />} />
                  <Route path="/device/:thingName" element={<DeviceDetail />} />
                  <Route path="/device/:thingName/shadow" element={<DeviceShadow />} />
                  <Route path="/device/:thingName/config" element={<DeviceConfig />} />
                  <Route path="/device/:thingName/logs" element={<DeviceLogs />} />
                  <Route path="/device/:thingName/commands" element={<CommandHistory />} />
                </Routes>
              </main>
            </div>
          </RequiredAuth>
        }
      />
    </Routes>
  );
}
