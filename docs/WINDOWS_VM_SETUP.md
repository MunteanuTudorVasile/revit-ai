# Testing the Add-in on a Mac: Windows VM Setup

Revit runs only on Windows. On an Apple silicon Mac you run **Windows 11 on Arm** in a virtual machine, install x64 Revit there (Windows runs it through its built-in x64 emulation), and build the add-in inside the VM.

Written for: Apple M5, 16 GB RAM, ~270 GB free disk. Facts checked October 2026; sources at the end.

Steps marked **👤 YOU** need you personally: passwords, licences, accounts, payments. Nobody else should do them for you.

## 1. UTM or Parallels?

| | Parallels Desktop | UTM |
|---|---|---|
| Cost | Paid subscription; free trial (14 days at the time of writing) | Free, open source |
| Listed by **Autodesk** for Revit 2025/2026 | **Yes**: "Parallels Desktop for Mac", "Any Apple silicon chip", Windows 11 | No |
| Authorized by **Microsoft** for Windows 11 on Arm | Yes (Microsoft's page names Parallels 18–20 on M1–M3) | No |
| 3D graphics in Windows | Yes (DirectX 11 / OpenGL through Parallels' display driver) | Experimental DirectX driver only in recent versions |
| Windows install | Built-in assistant downloads Windows 11 on Arm for you | You download the ISO and install SPICE guest tools |
| Mac folder sharing | Built in (`\\Mac\Home`) | SPICE WebDAV network drive (slower) |

**Recommendation: Parallels Desktop.** It is the configuration Autodesk lists for Revit on Apple silicon, so problems you hit are more likely to be ours and not the VM's. Start with the free trial; buy only if the add-in testing works. Use UTM only if you cannot pay, and expect Revit's graphics to be slow or glitchy; a failure there does not prove the add-in is broken.

## 2. Is this supported by Autodesk?

More than commonly assumed. Autodesk's official system requirements for **Revit 2025 and Revit 2026** each contain a "Parallels Desktop for Mac: Recommended-Level Configuration" listing:

- CPU: "Any Apple silicon chip"
- VM operating system: Windows 11 (for Apple silicon)
- VM memory: 16 GB
- Video: 4 GB video memory for the VM ("Automatic" graphics memory in Parallels)
- Graphics: Parallels virtual display adapter **without** Revit's "Use Hardware Acceleration" option
- Disk: 40 GB free minimum, 100 GB recommended

The pages do not say "Windows on Arm" or "emulation" explicitly. Revit is an x64 program, and on Apple silicon Windows 11 is always the Arm version, so Revit runs under Windows' x64 emulation. Expect it to be slower than on a comparable PC, and expect occasional emulation-specific bugs. Autodesk does not support UTM.

## 3. Sizing for a 16 GB Mac

Autodesk's 16 GB for the VM assumes a larger host (their newer Revit 2027 page asks for 32 GB host memory). With 16 GB total, macOS needs room too.

| Setting | Value | Why |
|---|---|---|
| VM memory | **8 GB** (try 10 GB if the Mac runs little else) | Leaves macOS 6–8 GB. Enough for small test projects like the ones in SMOKE_TESTS.md. |
| VM CPUs | **4** (up to 6) | Leaves cores for macOS. |
| Video memory | Automatic (Parallels) | Autodesk's advice. |
| Virtual disk | **128 GB**, expanding | Windows ~30 GB, each Revit year ~20–30 GB installed plus temp space. Expanding disks only use what is written. Install one Revit year first if space is tight. |

Close other heavy Mac apps while testing.

## 4. Install the VM (Parallels)

1. **👤 YOU** Download Parallels Desktop from parallels.com and install it. macOS asks for your **Mac admin password** and may ask you to allow system extensions in System Settings.
2. **👤 YOU** Start the trial (needs a **Parallels account**) or enter a licence you bought.
3. In the Installation Assistant choose **Get Windows 11 from Microsoft**. Parallels downloads Windows 11 on Arm legitimately and installs it with Parallels Tools.
4. Before the first boot (or later with the VM shut down): **Configure → Hardware**: 4 CPUs, 8 GB memory. **Configure → Options → Sharing**: share the Mac **Home folder** (or a custom folder containing `~/Projects/revit-ai`). Leave "Isolate Windows from Mac" off.
5. **👤 YOU** Windows setup: you choose the Windows user name and password, and whether to sign in with a **Microsoft account**.
6. **👤 YOU** Windows licence: Microsoft requires a separate Windows 11 Pro licence for each virtual machine. Activate in **Settings → System → Activation** with your own key or by buying one there.
7. In Parallels, if Windows looks blurry or tiny, turn off Retina resolution scaling (Autodesk's note about DPI in Revit).

### If you use UTM instead

1. **👤 YOU** Install UTM from getutm.app (free) or the Mac App Store (paid, same app).
2. Get the ISO legitimately: Microsoft's "Download Windows 11 Disk Image (ISO) for Arm-based PCs" page, or CrystalFetch, which downloads from Microsoft. It must be **Arm64**; an x64 ISO will not boot.
3. Create the VM: **Virtualize → Windows**, tick **Install drivers and SPICE tools**, 8 GB memory, 4 cores, 128 GB disk, and pick `~/Projects/revit-ai` (or your home folder) as the shared directory.
4. **👤 YOU** Windows setup, Microsoft account and licence as in steps 5–6 above.
5. After installing SPICE guest tools, the shared folder appears as a network drive in **This PC**. If not, run `C:\Program Files\SPICE webdavd\map-drive.bat`.

## 5. Install Revit in the VM

1. **👤 YOU** Sign in at autodesk.com with your **Autodesk account** and download Revit 2025 and/or 2026 (a subscription, an educational licence, or a free trial, whichever you are entitled to).
2. **👤 YOU** Run the installer in Windows. It asks for Windows admin approval (UAC).
3. **👤 YOU** Start Revit once and sign in to activate the **Revit licence**. Then close Revit.
4. In Revit **Options → Hardware**, turn off **Use Hardware Acceleration** if graphics misbehave (Autodesk's recommendation for Parallels).

## 6. Build and install the add-in

Find the shared repo in Windows File Explorer. With Parallels and the Home folder shared it is `\\Mac\Home\Projects\revit-ai`; with UTM it is on the WebDAV network drive.

Open **Windows PowerShell** (not as admin) and run:

```powershell
powershell -ExecutionPolicy Bypass -File \\Mac\Home\Projects\revit-ai\scripts\windows\setup-and-build.ps1
```

`-ExecutionPolicy Bypass` applies to this one run only; it changes no system setting.

The script:

1. checks for the .NET 8 SDK and **asks** before installing it with winget (**👤 YOU** answer winget's agreement prompt and the UAC prompt);
2. finds Revit 2025/2026 under `C:\Program Files\Autodesk\`;
3. stops if Revit is running;
4. copies the repo to `%USERPROFILE%\revit-ai-build` (building on the shared folder is slow and would mix Windows `obj\` files with the Mac's);
5. builds Debug for each installed Revit year, which deploys to `%APPDATA%\Autodesk\Revit\Addins\<year>\`;
6. checks the manifest and DLLs arrived, and prints next steps.

After changing code on the Mac: close Revit, run the script again.

## 7. Test

Start Revit, choose **Always Load** for Revit AI, and run [SMOKE_TESTS.md](SMOKE_TESTS.md) for each Revit year. Record "Parallels (or UTM), Windows 11 on Arm" in the Notes column so emulation-related failures can be told apart later. **👤 YOU** Phase 1 and 2 need your own OpenAI API key, entered in the add-in panel.

## Known limits

- Revit is x64 and runs under Windows on Arm emulation: slower startup and view regeneration than native x64.
- Microsoft's authorization page names Parallels 18–20 on M1–M3; it had not been updated to name the M5 or newer Parallels versions when checked.
- Windows on Arm does not support features that need nested virtualization (WSL, Windows Sandbox) and has DirectX 12 limitations. Neither is needed here.
- Autodesk's page says Revit **2026.5 and later require .NET 10**. Our add-in targets .NET 8. A .NET 8 add-in usually loads in a .NET 10 host, but this has not been tested; note the Revit build number in the smoke test results.
- A UTM run is not representative of what Autodesk supports.

## Sources

- Autodesk, [System requirements for Revit 2026 products](https://www.autodesk.com/support/technical/article/caas/sfdcarticles/sfdcarticles/System-requirements-for-Revit-2026-products.html) (Parallels / Apple silicon section, .NET 10 note)
- Autodesk, [System requirements for Revit 2025 products](https://www.autodesk.com/support/technical/article/caas/sfdcarticles/sfdcarticles/System-requirements-for-Revit-2025-products.html)
- Microsoft, [Options for using Windows 11 with Mac computers with Apple M1, M2, and M3 chips](https://support.microsoft.com/windows/options-for-using-windows-11-with-mac-computers-with-apple-m1-and-m2-chips-cd15fd62-9b34-4b78-b0bc-121baa3c568c)
- Microsoft, [Download Windows 11 Disk Image (ISO) for Arm-based PCs](https://www.microsoft.com/en-us/software-download/windows11arm64)
- UTM, [Windows installation guide](https://docs.getutm.app/guides/windows/) and [Windows guest support](https://docs.getutm.app/guest-support/windows/)
- UTM, [release notes on the experimental Windows DirectX driver](https://newreleases.io/project/github/utmapp/UTM/release/v5.0.6)
- Parallels, [Use Mac user files in Windows (KB 6912)](https://kb.parallels.com/6912)
- Parallels, [Revit on Mac: Parallels vs free alternatives](https://www.parallels.com/blogs/revit-mac-parallels-vs-free-alternatives/) (vendor source; quotes Autodesk's Revit 2027 requirements)
