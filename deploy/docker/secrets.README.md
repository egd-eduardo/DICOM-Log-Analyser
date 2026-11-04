# deploy/docker/secrets (never committed)

Create a `secrets` folder next to compose.yaml, readable only by the administrator (`chmod 700 secrets`):

| File | Content |
|---|---|
| `tls.crt`, `tls.key` | Certificate for the PUBLIC_ORIGIN host name, issued by your organisation's CA (PEM). |
| `oidc_client_secret` | Client secret of the application registered at your identity provider. |
| `cookie_secret` | 32 random bytes, base64: `openssl rand -base64 32 \| tr -- '+/' '-_' > secrets/cookie_secret` |
| `allowed-emails.txt` | One allowed e-mail address per line (may be empty when ALLOWED_EMAIL_DOMAINS is used). |

These are credentials of the access proxy. The analyser itself has no passwords or secrets.
