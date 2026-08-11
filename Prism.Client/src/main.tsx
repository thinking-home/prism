import React from "react";
import ReactDOM from "react-dom/client";
import { HashRouter } from "react-router";
import { App } from "./App";
import { ServerUrlProvider } from "./serverUrl";
import "./styles.css";

ReactDOM.createRoot(document.getElementById("root")!).render(
  <React.StrictMode>
    <ServerUrlProvider>
      <HashRouter>
        <App />
      </HashRouter>
    </ServerUrlProvider>
  </React.StrictMode>,
);
