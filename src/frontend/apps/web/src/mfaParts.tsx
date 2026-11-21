import type { EnrolmentSecret } from "@hasm/api-client";
import { useI18n } from "@hasm/ui";

/**
 * The provisioning secret of a new authenticator, shown ONCE and only as text (the provisioning link and the Base32 key; no image is generated, so nothing about the secret leaves the page). One component for the login
 * enrolment and the voluntary enrolment in the security settings. It holds nothing: the caller drops the secret as soon as the enrolment ends.
 */
export function EnrolmentSecretBlock({ secret }: { secret: EnrolmentSecret }) {
  const { t } = useI18n();
  return (
    <dl className="kv">
      <dt>{t("mfa.enrol.secret")}</dt>
      <dd><code data-testid="mfa-secret" dir="ltr">{secret.secret}</code></dd>
      <dt>{t("mfa.enrol.uri")}</dt>
      <dd><a href={secret.otpauthUri} data-testid="mfa-uri" dir="ltr">{secret.otpauthUri}</a></dd>
    </dl>
  );
}
