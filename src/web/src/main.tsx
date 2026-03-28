import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { BrowserRouter, useNavigate } from "react-router-dom";
import { Security } from "@okta/okta-react";
import oktaAuth from "./auth/oktaConfig";
import App from "./App";
import "./index.css";

function OktaWrapper() {
  const navigate = useNavigate();
  const restoreOriginalUri = async (
    _oktaAuth: typeof oktaAuth,
    originalUri: string,
  ) => {
    navigate(originalUri || "/", { replace: true });
  };

  return (
    <Security oktaAuth={oktaAuth} restoreOriginalUri={restoreOriginalUri}>
      <App />
    </Security>
  );
}

createRoot(document.getElementById("root")!).render(
  <StrictMode>
    <BrowserRouter>
      <OktaWrapper />
    </BrowserRouter>
  </StrictMode>,
);
