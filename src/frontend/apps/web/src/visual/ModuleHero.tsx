import type { ReactNode } from "react";
import { ModuleIllustration, type IllustrationKind } from "./ModuleIllustration";

/** Contextual page header: icon + title + description, an optional tag, page actions, and the module illustration at the inline-end edge. */
export function ModuleHero({ module, icon, title, description, tag, actions, as: Heading = "h1" }: {
  module: IllustrationKind; icon?: ReactNode; title: string; description?: string; tag?: ReactNode; actions?: ReactNode;
  /** Heading level; the page-level hero is the h1 (and the page title), embedded heroes use a lower level. */
  as?: "h1" | "h2" | "h3";
}) {
  return (
    <header className="module-hero">
      <div className="hero-body">
        {icon && <span className="hero-icon" aria-hidden="true">{icon}</span>}
        <div className="hero-text">
          <Heading id={Heading === "h1" ? "page-title" : undefined}>{title}</Heading>
          {description && <p className="page-description">{description}</p>}
          {tag && <div className="hero-tag">{tag}</div>}
          {actions && <div className="hero-actions">{actions}</div>}
        </div>
      </div>
      <ModuleIllustration kind={module} />
    </header>
  );
}
