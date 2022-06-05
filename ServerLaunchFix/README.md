# Server Launch Fix

Enables mods for single player and host & play.

## Why is this necessary?

When launching a single player game or a host & play session, V Rising will
launch a dedicated server as a separate process. By default the automatically
launched server does not have mods installed on it, and as such you'd have to
manually install mods both to the client as well as the bundled server to
actually use them.

## What does Server Launch Fix do?

Server Launch Fix will configure the server to use the same mods which have been
installed on the client, meaning you need to install mods only to a single
location.

The way this happens is by automatically copying the mod loader from the client
to the server before launch and configuring it to load mods from the same
location as the client. In addition a `BepInEx_Server` directory is created,
which is used by the server to store state such as cache & unhollowed DLLs. The
mods themselves only reside in one place and are used by the server via
symlinks.

When installed on a server, this mod does nothing.

## Limitations

- On first launch of the server, it will likely time out connecting to it, as it
  has to build the unhollowed DLL cache which is required by mods.
  - If/when it timeouts, just attempt to re-launch a few times. If the issue
    persists for more than 4 attempts, reach out on Discord for support.
- Does not work on non-NTFS file systems.
- **The doorstop_config.ini of the server will be overwritten on every launch**
- **The BepInEx.cfg of the server will be overwritten on every launch**
