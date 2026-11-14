import { ApiError } from "@hasm/api-client";
import { useI18n } from "@hasm/ui";
import { useEffect, useRef, type ReactNode } from "react";

export type BannerKind = "error" | "success" | "warning" | "info";

/** Errors are announced (role=alert); other messages politely (role=status). */
export function Banner({ kind, children, testId }: { kind: BannerKind; children: ReactNode; testId?: string }) {
  return (
    <div className={`banner banner-${kind}`} role={kind === "error" ? "alert" : "status"} data-testid={testId}>
      {children}
    </div>
  );
}

/** Consistent page title block: heading (also the aria label of the page section), optional one-line description and page-level actions. */
export function PageHeader({ title, description, actions }: { title: string; description?: string; actions?: ReactNode }) {
  return (
    <div className="page-header">
      <div>
        <h1 id="page-title">{title}</h1>
        {description && <p className="page-description">{description}</p>}
      </div>
      {actions && <div className="page-actions">{actions}</div>}
    </div>
  );
}

/** A titled surface that groups related content. Renders a labelled group when it has a title. */
export function Card({ title, hint, children, className = "" }: { title?: string; hint?: string; children: ReactNode; className?: string }) {
  const id = title ? `card-${title.replace(/\s+/g, "-")}` : undefined;
  return (
    <section className={`card ${className}`.trim()} aria-labelledby={id}>
      {title && <h2 id={id} className="card-title">{title}</h2>}
      {hint && <p className="hint card-hint">{hint}</p>}
      {children}
    </section>
  );
}

export function Loading() {
  const { t } = useI18n();
  return <p role="status" className="loading">{t("loading")}</p>;
}

interface FieldProps {
  id: string;
  label: string;
  error?: string;
  hint?: string;
  required?: boolean;
  children: (aria: { id: string; "aria-invalid": boolean; "aria-describedby"?: string; "aria-required": boolean }) => ReactNode;
}

/** A labelled control with an associated, announced error message (WCAG: label, programmatic error, not colour alone). */
export function Field({ id, label, error, hint, required, children }: FieldProps) {
  const described = [error ? `${id}-error` : null, hint ? `${id}-hint` : null].filter(Boolean).join(" ") || undefined;
  return (
    <div className={`field${error ? " field-invalid" : ""}`}>
      <label htmlFor={id}>{label}{required && <span aria-hidden="true" className="required"> *</span>}</label>
      {children({ id, "aria-invalid": !!error, "aria-describedby": described, "aria-required": !!required })}
      {hint && <p id={`${id}-hint`} className="hint">{hint}</p>}
      {error && <p id={`${id}-error`} className="field-error">{error}</p>}
    </div>
  );
}

interface ConfirmProps {
  title: string;
  message: string;
  confirmLabel: string;
  /** Defaults to "Cancel"; the unsaved-changes dialog uses "Stay". */
  cancelLabel?: string;
  onConfirm: () => void;
  onCancel: () => void;
  /** Extra content between the message and the buttons (for example a choice). */
  children?: ReactNode;
}

/** Confirmation for sensitive actions. Focus starts on Cancel (the safe choice); Escape cancels. */
export function ConfirmDialog({ title, message, confirmLabel, cancelLabel, onConfirm, onCancel, children }: ConfirmProps) {
  const { t } = useI18n();
  const cancel = useRef<HTMLButtonElement>(null);
  useEffect(() => cancel.current?.focus(), []);
  return (
    <div className="dialog-backdrop">
      <div role="dialog" aria-modal="true" aria-labelledby="confirm-title" aria-describedby="confirm-message" className="dialog"
        onKeyDown={(e) => { if (e.key === "Escape") onCancel(); }}>
        <h2 id="confirm-title">{title}</h2>
        <p id="confirm-message">{message}</p>
        {children}
        <div className="actions">
          <button type="button" ref={cancel} onClick={onCancel}>{cancelLabel ?? t("cancel")}</button>
          <button type="button" className="danger" onClick={onConfirm}>{confirmLabel}</button>
        </div>
      </div>
    </div>
  );
}

/** Maps a failed call to a translated message. Unknown codes fall back to a generic message by status; no server text is ever shown. */
export function errorText(t: (k: string) => string, e: unknown, keyPrefix = "error."): string {
  if (e instanceof ApiError) {
    const specific = `${keyPrefix}${e.code}`;
    const translated = t(specific);
    if (translated !== specific) return translated;
    if (e.status === 403) return t("error.forbidden");
    if (e.status === 401) return t("error.session_expired");
    if (e.status === 503) return t("error.company_unavailable");
    return t("error.generic");
  }
  return t("error.network");
}

export function fieldErrors(t: (k: string) => string, e: unknown): Record<string, string> {
  const out: Record<string, string> = {};
  if (e instanceof ApiError) for (const f of e.details.fields ?? []) {
    const key = `field.${f.code}`;
    out[f.field] = t(key) === key ? t("field.invalid") : t(key);
  }
  return out;
}
