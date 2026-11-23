# Installation guide: Fileodon Distribution Listener

This guide covers local development and the recommended Linux installation using
systemd. The supplied installer creates a dedicated service account, downloads the
Fileodon Master API public key, and starts the listener.

## Prerequisites

For local development:

- .NET 10 SDK
- A PEM-encoded Fileodon Master API public key
- Access to test SFTP servers

For a production Linux host:

- Linux with systemd
- `curl` and `systemctl`
- `sudo` or root access
- Network access to `api.fileodon.dev` and the SFTP servers

The service is published as framework-dependent and therefore requires the .NET 10
runtime on the Linux host.

## Local installation

From the repository directory:

```bash
dotnet restore
dotnet build FileodonDistributionListener/FileodonDistributionListener.csproj
```

Place the Master API public key in the project content root as `public.pem`, or set
the path explicitly:

```bash
export Jwt__PublicKeyPath=/etc/fileodon/public.pem
dotnet run --project FileodonDistributionListener/FileodonDistributionListener
```

On Windows PowerShell, use:

```powershell
$env:Jwt__PublicKeyPath = "C:\path\to\public.pem"
dotnet run --project .\FileodonDistributionListener\FileodonDistributionListener
```

The default development URL is <http://localhost:5096>. Verify it with:

```bash
curl --fail http://localhost:5096/health
```

## Production installation with the supplied script

Run the script from a Linux checkout of this repository:

```bash
sudo ./install-fileodon-distribution-listener.sh
```

The default installation:

- publishes to `/opt/fileodon-distribution-listener`
- creates the `fileodon` system user
- downloads the public key to `/opt/fileodon-distribution-listener/public.pem`
- listens on port `5096`
- creates and enables `fileodon-distribution-listener.service`
- starts the service immediately

The script runs `dotnet publish` with `-c Release -r linux-x64
--self-contained false`. Install the .NET 10 runtime before running it.

### Use an existing publish directory

Build on another machine and transfer the publish output to the Linux host:

```powershell
dotnet publish .\FileodonDistributionListener\FileodonDistributionListener.csproj `
  -c Release -r linux-x64 --self-contained false `
  -o .\artifacts\linux-x64
```

Then install it without rebuilding:

```bash
sudo ./install-fileodon-distribution-listener.sh \
  --build-dir /path/to/artifacts/linux-x64
```

`--skip-build` can also be used with `BUILD_DIR=/path/to/publish`. The directory
must contain `FileodonDistributionListener.dll`.

### Installer options

The following environment variables customize the installation:

| Variable | Default | Purpose |
| --- | --- | --- |
| `INSTALL_DIR` | `/opt/fileodon-distribution-listener` | Published application directory |
| `BUILD_DIR` | unset | Existing publish directory |
| `PUBLIC_KEY_URL` | Fileodon Master API public-key endpoint | Public key download URL |
| `PUBLIC_KEY_PATH` | `$INSTALL_DIR/public.pem` | Public key destination |
| `LISTEN_PORT` | `5096` | HTTP listening port |
| `SERVICE_NAME` | `fileodon-distribution-listener` | systemd service name |
| `SERVICE_USER` | `fileodon` | Service account |

Example:

```bash
sudo LISTEN_PORT=5196 SERVICE_USER=fileodon-listener \
  ./install-fileodon-distribution-listener.sh
```

The installer checks that the downloaded file contains a PEM public-key header
before creating the service.

## Verify and operate the service

Check status and recent logs:

```bash
sudo systemctl status fileodon-distribution-listener --no-pager
sudo journalctl -u fileodon-distribution-listener -n 50 --no-pager
```

Check the health endpoint:

```bash
curl --fail http://127.0.0.1:5096/health
```

When `LISTEN_PORT` is changed, use that port in the health URL and firewall rules.
The installer does not configure a firewall or TLS certificate. Put the listener
behind a TLS-terminating reverse proxy if it must be accessed over an untrusted
network.

## Updating

Run the installer again with the same options. It replaces the publish directory,
refreshes the public key, reloads systemd, and restarts the service:

```bash
sudo ./install-fileodon-distribution-listener.sh
```

Jobs are held in memory only. Do not update during an active transfer unless it is
acceptable for that transfer to be interrupted.

## Troubleshooting

### The application cannot find the JWT public key

Confirm that the configured path exists and contains a PEM-encoded public key:

```bash
sudo ls -l /opt/fileodon-distribution-listener/public.pem
sudo grep "BEGIN" /opt/fileodon-distribution-listener/public.pem
```

Set `PUBLIC_KEY_PATH` during installation or `Jwt__PublicKeyPath` in the generated
production configuration if the key is stored elsewhere.

### The service fails to start

Read the service log and confirm that the .NET 10 runtime is installed:

```bash
sudo journalctl -u fileodon-distribution-listener -b --no-pager
dotnet --list-runtimes
```

### A transfer remains failed

Poll the job and inspect its `error` and per-file `files[].error` values. Check that
both SFTP hosts are reachable, the usernames and private keys are valid, and the
remote paths exist or are writable. The listener intentionally trusts the SSH host
key presented by each endpoint, so restrict endpoint access to trusted hosts.

### The health endpoint is unreachable

Confirm the configured port, service status, and firewall or reverse-proxy rules:

```bash
sudo ss -ltnp | grep 5096
sudo systemctl status fileodon-distribution-listener --no-pager
```

## Security notes

- Treat transfer requests as sensitive: they contain SFTP private keys.
- Use HTTPS or a private network between the Fileodon API and listener.
- Keep `public.pem`, logs, and service configuration readable only as required.
- Do not commit keys, bearer tokens, or production configuration to the repository.
- Run the service under the dedicated unprivileged `fileodon` account.

