import { Routes, Route, NavLink } from "react-router-dom";
import { Dog, Video, Search, Activity, LayoutDashboard } from "lucide-react";
import Dashboard from "./pages/Dashboard";
import ClipsBrowser from "./pages/ClipsBrowser";
import ClipDetail from "./pages/ClipDetail";
import Detections from "./pages/Detections";
import SystemHealthPage from "./pages/SystemHealth";

const navItems = [
  { to: "/", icon: LayoutDashboard, label: "Dashboard" },
  { to: "/clips", icon: Video, label: "Clips" },
  { to: "/detections", icon: Search, label: "Detections" },
  { to: "/health", icon: Activity, label: "System" },
];

export default function App() {
  return (
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
      </nav>

      <main className="flex-1 p-6 overflow-y-auto">
        <Routes>
          <Route path="/" element={<Dashboard />} />
          <Route path="/clips" element={<ClipsBrowser />} />
          <Route path="/clips/:id" element={<ClipDetail />} />
          <Route path="/detections" element={<Detections />} />
          <Route path="/health" element={<SystemHealthPage />} />
        </Routes>
      </main>
    </div>
  );
}
