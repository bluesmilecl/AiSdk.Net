# Security Policy

**English** · [Español](#-español)

## Supported versions

AiSdk.Net is at version 0.x. Security fixes are released for the **latest published version** only.

| Version | Supported |
|---|---|
| Latest 0.x | ✅ |
| Older | ❌ |

## Reporting a vulnerability

**Please do not open a public issue for security problems.**

Report it privately through GitHub:
[**Report a vulnerability**](https://github.com/bluesmilecl/AiSdk.Net/security/advisories/new)
(Security tab → *Report a vulnerability*). Include:

- the affected package(s) and version
- a description of the issue and its impact
- steps or a minimal code sample to reproduce it

You can expect an acknowledgment within **5 business days**. We will keep you informed while we work on a fix and
will credit you in the advisory unless you prefer to stay anonymous.

## Scope notes

Areas where AiSdk.Net handles untrusted input and where reports are especially welcome:

- Skills loaded from GitHub, zip files or directories (path traversal, script execution)
- MCP servers and tool results returned to the model
- Webhook triggers in `AiSdk.Net.Workflows` (signature verification) and workflow events sent over HTTP (approvals)
- Tool approvals with `useChat` (signed `approvalId`s, `UIMessageConverter.ToModelInput`)
- Document converters (PDF, Office, HTML, images) and the external tools they call

Prompt injection in model outputs is a general risk of LLM applications; reports are welcome when the SDK's
defaults make it worse than necessary.

---

## 🇪🇸 Español

**No abras un issue público para problemas de seguridad.** Repórtalos en privado con
[**Report a vulnerability**](https://github.com/bluesmilecl/AiSdk.Net/security/advisories/new)
(pestaña Security → *Report a vulnerability*), indicando el paquete y la versión afectados, el impacto y los pasos
para reproducirlo. Responderemos en un plazo de **5 días hábiles** y te daremos crédito en el aviso, salvo que
prefieras el anonimato. Solo la **última versión publicada** recibe correcciones de seguridad.
