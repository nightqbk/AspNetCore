## Using MsQuic on Windows

### Setup pre-requisites

1. Update machine to the latest Windows Insiders build (build number 19010 or later). This is required for TLS 1.3 support.
2. Copy msquic.dll and msquic.pdb to this directory and uncomment the copy task in Microsoft.AspNetCore.Server.Kestrel.Transport.MsQuic.csproj. This will copy the msquic.dll into any built project.

For external contributors, msquic.dll isn't available publicly yet. See https://github.com/aspnet/Announcements/issues/393.

Credit to Diwakar Mantha and the Kaizala team for the MsQuic interop code.