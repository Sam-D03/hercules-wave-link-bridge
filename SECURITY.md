# Security

Please report security issues privately through GitHub's **Report a
vulnerability** feature rather than opening a public issue.

The bridge communicates only with local Windows APIs, the locally running Wave
Link WebSocket service, and the connected Hercules device. Spotify artwork is
read from Windows Global Media Controls; no Spotify account token is used.

Release installers are currently unsigned. Verify the SHA-256 checksum listed
on the GitHub release before running a downloaded installer.
