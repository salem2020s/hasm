import { I18nProvider } from "@hasm/ui";
import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { App } from "./App";
import { catalog } from "./messages";

createRoot(document.getElementById("root")!).render(
  <StrictMode>
    <I18nProvider catalog={catalog} defaultLocale="ar">
      <App />
    </I18nProvider>
  </StrictMode>,
);
