# MotionClip kiosk deployment

Three tracked pieces, and one you supply.

| | |
|---|---|
| `install-kiosk-task.ps1` | Registers the scheduled task that starts MotionClip at logon. Idempotent — re-running picks up argument changes. |
| `uninstall-kiosk-task.ps1` | The symmetric inverse. Stops and unregisters the task, kills the process, removes the install and log directories. Clip output is preserved unless you pass `-RemoveClips`. |
| `../roamfile.example.yaml` | Template deployment manifest for [roam](https://github.com/charles8051/roam), with the operational notes worth keeping. |
| `../roamfile.yaml` | **Yours.** Not in this repo — see below. |

Run `Get-Help .\install-kiosk-task.ps1 -Full` for the parameters. Both scripts
are edited as a pair: when the installer learns to write somewhere new or
register another side effect, the inverse goes in the uninstaller. Nothing
tracks side effects per-deployment, so the pair *is* the inventory.

## Why there is no real roamfile here

A roam manifest names hosts and the local accounts on them. That is site
information — it describes a particular fleet, not this library — so
`roamfile*.yaml` is gitignored, with `roamfile.example.yaml` negated so the
template stays tracked and reviewable.

Copy the template, fill in your hosts, and keep the result wherever you keep
the rest of your deployment inventory. A private repo is the right home for
it; this one is a media library.

## Installing without roam

The scripts stand alone. Publish, copy the output to the target, and run the
installer there:

```powershell
dotnet publish -c Release -r win-x64 --self-contained
# copy the publish output to the box, then on the box:
.\scripts\install-kiosk-task.ps1 -User 'MACHINE\kiosk-account' -LogLevel info
```

Pass `-User` explicitly whenever you install from a different account than the
one the task runs as — an elevated or SSH session, typically. The clip and log
directories default to that account's profile, resolved through its SID rather
than assumed from its name, so an administrator-run install still writes into
the kiosk account's profile. If the account has never logged on it has no
profile yet, and the installer will stop and ask for `-OutputDir` and
`-LogDir`.
