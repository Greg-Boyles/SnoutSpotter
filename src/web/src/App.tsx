import { useEffect, useState } from "react";
import { Routes, Route, NavLink, useLocation } from "react-router-dom";
import { Dog, Video, Search, Activity, LayoutDashboard, LogOut, Radio, Tag, Cpu, Package, Menu, X, GraduationCap, HardDriveDownload, Settings, PawPrint, ChevronDown, Plug, Camera } from "lucide-react";
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
import ServerSettings from "./pages/ServerSettings";
import PiPackages from "./pages/PiPackages";
import TrainingJobs from "./pages/TrainingJobs";
import SubmitTraining from "./pages/SubmitTraining";
import TrainingJobDetail from "./pages/TrainingJobDetail";
import TrainingAgentDetail from "./pages/TrainingAgentDetail";
import Pets from "./pages/Pets";
import Devices from "./pages/Devices";
import Integrations from "./pages/Integrations";
import { setAuthGetter, setHouseholdGetter } from "./api";
import HouseholdProvider from "./components/HouseholdProvider";
import { useHousehold } from "./hooks/useHousehold";

type NavItem = { to: string; icon: React.ElementType; label: string };
type NavGroup = { heading: string; items: NavItem[] };

const navGroups: NavGroup[] = [
  {
    heading: "Browse",
    items: [
      { to: "/", icon: LayoutDashboard, label: "Dashboard" },
      { to: "/clips", icon: Video, label: "Clips" },
      { to: "/detections", icon: Search, label: "Activity" },
      { to: "/pets", icon: PawPrint, label: "Pets" },
      { to: "/live", icon: Radio, label: "Live View" },
    ],
  },
  {
    heading: "ML Pipeline",
    items: [
      { to: "/labels", icon: Tag, label: "Labels" },
      { to: "/exports", icon: Package, label: "Datasets" },
      { to: "/models", icon: Cpu, label: "ML Models" },
      { to: "/training", icon: GraduationCap, label: "Training" },
    ],
  },
  {
    heading: "Devices",
    items: [
      { to: "/devices", icon: Camera, label: "Device Registry" },
      { to: "/health", icon: Activity, label: "System Health" },
      { to: "/pi-packages", icon: HardDriveDownload, label: "Pi Releases" },
      { to: "/settings", icon: Settings, label: "Server Config" },
    ],
  },
  {
    heading: "Integrations",
    items: [
      { to: "/integrations", icon: Plug, label: "Connectors" },
    ],
  },
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

function AppLayout() {
  const { oktaAuth } = useOktaAuth();
  const [sidebarOpen, setSidebarOpen] = useState(false);
  const location = useLocation();
  const { activeHousehold, households, setActiveHousehold } = useHousehold();
  const [hhDropdownOpen, setHhDropdownOpen] = useState(false);

  useEffect(() => {
    setSidebarOpen(false);
  }, [location.pathname]);

  return (
    <div className="flex min-h-screen bg-gray-50">
      {/* Mobile header */}
      <div className="fixed top-0 left-0 right-0 z-30 flex items-center justify-between bg-white border-b border-gray-200 px-4 py-3 md:hidden">
        <div className="flex items-center gap-2">
          <Dog className="w-6 h-6 text-amber-600" />
          <span className="text-lg font-bold text-gray-900">SnoutSpotter</span>
        </div>
        <button
          onClick={() => setSidebarOpen(!sidebarOpen)}
          className="p-1.5 rounded-lg text-gray-600 hover:bg-gray-100"
          aria-label="Toggle menu"
        >
          {sidebarOpen ? <X className="w-6 h-6" /> : <Menu className="w-6 h-6" />}
        </button>
      </div>

      {/* Backdrop */}
      {sidebarOpen && (
        <div
          className="fixed inset-0 z-30 bg-black bg-opacity-50 md:hidden"
          onClick={() => setSidebarOpen(false)}
        />
      )}

      {/* Sidebar */}
      <nav className={`
        fixed inset-y-0 left-0 z-40 w-56 bg-white border-r border-gray-200 flex flex-col
        transform transition-transform duration-200 ease-in-out
        ${sidebarOpen ? "translate-x-0" : "-translate-x-full"}
        md:translate-x-0 md:static md:z-auto
      `}>
        <div className="px-4 py-5 border-b border-gray-200">
          <div className="flex items-center gap-2">
            <Dog className="w-7 h-7 text-amber-600" />
            <span className="text-lg font-bold text-gray-900">SnoutSpotter</span>
          </div>
          {activeHousehold && (
            <div className="relative mt-2">
              <button
                onClick={() => households.length > 1 && setHhDropdownOpen(!hhDropdownOpen)}
                className={`flex items-center gap-1 text-xs text-gray-500 ${households.length > 1 ? "hover:text-gray-700 cursor-pointer" : ""}`}
              >
                {activeHousehold.name}
                {households.length > 1 && <ChevronDown className="w-3 h-3" />}
              </button>
              {hhDropdownOpen && households.length > 1 && (
                <div className="absolute left-0 top-full mt-1 w-48 bg-white border border-gray-200 rounded-lg shadow-lg z-50">
                  {households.map((hh) => (
                    <button
                      key={hh.householdId}
                      onClick={() => { setHhDropdownOpen(false); setActiveHousehold(hh.householdId); }}
                      className={`block w-full text-left px-3 py-2 text-xs hover:bg-gray-50 ${
                        hh.householdId === activeHousehold.householdId ? "text-amber-600 font-medium" : "text-gray-600"
                      }`}
                    >
                      {hh.name}
                    </button>
                  ))}
                </div>
              )}
            </div>
          )}
        </div>
        <div className="flex-1 px-2 py-4 overflow-y-auto">
          {navGroups.map((group, gi) => (
            <div key={group.heading} className={gi > 0 ? "mt-4" : ""}>
              <p className="px-3 mb-1 text-xs font-semibold text-gray-400 uppercase tracking-wider">{group.heading}</p>
              <ul className="space-y-0.5">
                {group.items.map(({ to, icon: Icon, label }) => (
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
            </div>
          ))}
        </div>
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

      <main className="flex-1 p-4 pt-16 md:p-6 md:pt-6 overflow-y-auto">
        <Routes>
          <Route path="/" element={<Dashboard />} />
          <Route path="/clips" element={<ClipsBrowser />} />
          <Route path="/clips/:id" element={<ClipDetail />} />
          <Route path="/detections" element={<Detections />} />
          <Route path="/pets" element={<Pets />} />
          <Route path="/devices" element={<Devices />} />
          <Route path="/integrations" element={<Integrations />} />
          <Route path="/live" element={<LiveView />} />
          <Route path="/labels/:keyframeKey" element={<LabelDetail />} />
          <Route path="/labels" element={<Labels />} />
          <Route path="/exports" element={<TrainingExports />} />
          <Route path="/models" element={<Models />} />
          <Route path="/settings" element={<ServerSettings />} />
          <Route path="/training" element={<TrainingJobs />} />
          <Route path="/training/new" element={<SubmitTraining />} />
          <Route path="/training/agents/:thingName" element={<TrainingAgentDetail />} />
          <Route path="/training/:jobId" element={<TrainingJobDetail />} />
          <Route path="/pi-packages" element={<PiPackages />} />
          <Route path="/health" element={<SystemHealthPage />} />
          <Route path="/device/:thingName" element={<DeviceDetail />} />
          <Route path="/device/:thingName/shadow" element={<DeviceShadow />} />
          <Route path="/device/:thingName/config" element={<DeviceConfig />} />
          <Route path="/device/:thingName/logs" element={<DeviceLogs />} />
          <Route path="/device/:thingName/commands" element={<CommandHistory />} />
        </Routes>
      </main>
    </div>
  );
}

export default function App() {
  const { oktaAuth } = useOktaAuth();

  useEffect(() => {
    setAuthGetter(() => oktaAuth.getAccessToken());
    setHouseholdGetter(() => localStorage.getItem("activeHouseholdId") ?? undefined);
  }, [oktaAuth]);

  return (
    <Routes>
      <Route path="/login/callback" element={<LoginCallback />} />
      <Route
        path="*"
        element={
          <RequiredAuth>
            <HouseholdProvider>
              <AppLayout />
            </HouseholdProvider>
          </RequiredAuth>
        }
      />
    </Routes>
  );
}
