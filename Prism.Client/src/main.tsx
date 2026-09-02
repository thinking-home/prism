import React from "react";
import ReactDOM from "react-dom/client";
import { HashRouter } from "react-router";
import { MantineProvider } from "@mantine/core";
import "@mantine/core/styles.css";
import { App } from "./App";
import { ServerUrlProvider } from "./serverUrl";

// Весь интерфейс собран из компонентов Mantine: своей вёрстки и своих стилей в
// проекте нет — тему и оформление целиком ведёт библиотека. Схема тёмная по
// умолчанию, как было у прежнего интерфейса.
ReactDOM.createRoot(document.getElementById("root")!).render(
  <React.StrictMode>
    <MantineProvider defaultColorScheme="dark">
      <ServerUrlProvider>
        <HashRouter>
          <App />
        </HashRouter>
      </ServerUrlProvider>
    </MantineProvider>
  </React.StrictMode>,
);
