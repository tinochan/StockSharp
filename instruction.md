# Instruction: Running the LiveOptionsQuoting Sample (JetBrains Rider + Windows VM via UTM)

This project is a **WPF application** that targets `net10.0-windows`. WPF apps can only **run on Windows**, which means:

- On a **Windows machine or VM**: you can open, build, and run it directly in JetBrains Rider.
- On **macOS**: Rider can open, edit, and *build* the project, but to actually *run* the UI you need Windows — set up a Windows VM with UTM (Part 2).

---

## Part 1 — Run the Project in JetBrains Rider

### Prerequisites
- **JetBrains Rider** (2023.3 or later — needed for `.slnx` solution support and .NET 10).
- **.NET 10 SDK** — download from <https://dotnet.microsoft.com/en-us/download>:
  - On Windows: install the Windows x64 installer.
  - On macOS: install the macOS (arm64 / x64) installer.

### Steps

1. **Install JetBrains Rider** if you don't have it yet.

2. **Install the .NET 10 SDK** (see Prerequisites above). Rider picks it up automatically after installation; restart Rider if needed.

3. **Open the solution**: in Rider, go to **File → Open…** and select `StockSharp.slnx` at the root of the repository (`/Users/tino/GitHub/StockSharp/StockSharp.slnx`).

4. **Wait for the solution to load.** The first load triggers a full NuGet restore across all projects and can take several minutes. Check the "Build" / "NuGet" tool windows for progress and errors.

5. **Select the sample as the run target.** This sample ships with two project files:
   - `09_Strategies.LiveOptionsQuoting.csproj` — uses the `StockSharp.Xaml.Charting` **NuGet package** (simpler, no source build needed).
   - `09_Strategies.LiveOptionsQuoting_fromsrc.csproj` — builds Xaml.Charting **from the local source** via a project reference.
   - In the run-configuration dropdown (top-right of the Rider toolbar), pick one of them, e.g. `09_Strategies.LiveOptionsQuoting`.

6. **Build the solution**: **Build → Build Solution** (or `Ctrl + F9` on Windows / `Cmd + F9` on macOS). Fix any compile errors reported in the Build tool window.

7. **Run the app**: **Run → Run 'StockSharp.Samples.Strategies.LiveOptionsQuoting'** (or press `Shift + F10`).
   - **On Windows**: the WPF window (Live Options Quoting terminal) opens and the strategy starts on the dummy provider.
   - **On macOS**: the build succeeds, but *running* fails with a platform error because WPF is Windows-only. Use the Windows VM from Part 2.

---

## Part 2 — Set Up a Windows VM with UTM (for Apple Silicon / Intel Macs)

Because this sample is a WPF app, the easiest way to run it on a Mac is inside a Windows virtual machine.

### Step 1: Install UTM
1. Go to the official website: <https://mac.getutm.app>.
2. Click **Download** to get the free version (or purchase it from the Mac App Store if you want automatic updates).
3. Open the downloaded `.dmg` file and drag **UTM** into your **Applications** folder.

### Step 2: Download the Windows ISO
Because Windows handles ARM (Apple Silicon) and x86 (Intel) differently, UTM provides a built-in shortcut to get the correct version:
1. Open **UTM** and click **Create a New Virtual Machine**.
2. Select **Virtualize** (if you have an Apple Silicon Mac — it's much faster) or **Emulate** (if you are running a different architecture, though Virtualize is highly recommended for M-series chips).
3. Click on **Windows**.
4. Check the box that says **"Fetch latest Windows installer from Microsoft"**.
5. Click **Download**. This will automatically download the correct, bootable Windows ISO image for your Mac's architecture.

### Step 3: Configure the VM Settings
Once the download finishes, UTM will guide you through the configuration wizard:
- **Hardware**: Allocate at least **4 GB of RAM** (8 GB is better if your Mac has 16 GB or more) and **4 CPU cores**.
- **Storage**: Allocate at least **64 GB** of drive space.
- **Spice Tools**: Leave the checkmark enabled for **"Install SPICE tools and drivers"**. This is critical for getting the Windows UI, internet, and mouse to work smoothly later.
- Click **Save** to finish the setup.

### Step 4: Install Windows and SPICE Tools (The UI Fix)
1. In the UTM sidebar, click the **Play** button on your new Windows VM.
2. Follow the standard Windows setup prompts (Language, Keyboard, "I don't have a product key").
3. Once Windows boots to the desktop for the first time, you might notice the resolution is low, the mouse is laggy, and there is no internet. **This is normal.**
4. Open **File Explorer** in Windows.
5. Go to **This PC** and look for the virtual CD Drive named **SPICE Guest Tools**.
6. Open it and run **spice-guest-tools-xxx.exe**.
7. Follow the installer prompts and **restart the virtual machine** when finished.
8. Once restarted, your network, proper screen resolution, graphics acceleration, and fluid UI will all work perfectly.

---

## After the VM Is Ready

To run the sample inside the Windows VM:

1. **Install JetBrains Rider** and the **.NET 10 SDK** inside the Windows VM (see Part 1 prerequisites).
2. **Get the code into the VM** — either clone it inside the VM, or map your local repository as a shared folder (Part 3).
3. Open `StockSharp.slnx` in Rider **inside the VM** and follow Part 1 — build and run from there.

> Tip: Use the shared-folder mapping from Part 3 so you can keep editing on the Mac with Rider and rebuild/run inside the VM without copying files back and forth.

---

## Part 3 — Map the StockSharp Repository to the Windows VM

### Step 1: Configure the Shared Folder in UTM (Mac Side)
1. **Completely shut down** your Windows virtual machine.
2. Open **UTM**, select your Windows VM from the left sidebar, and click the **Settings** icon (gear) in the top-right corner.
3. Scroll down the left menu and click on **Sharing**.
4. Set the **Directory Share Mode** dropdown to **SPICE WebDAV**.
5. Click the **+ (Add)** button or **Browse** button.
6. Navigate to and select your **StockSharp repository folder** on your Mac.
7. Click **Save**.

### Step 2: Access and Map the Repository inside Windows
1. **Start your Windows VM** up.
2. Open **File Explorer** in Windows.
3. Click on **This PC** in the left sidebar.
4. Look under the **Network locations** section. You should see a drive labeled **DavWWWRoot** (`\\localhost@9843`) or similar — that is your mapped StockSharp folder.

---

## Part 4 — Compile the Repo into a Standalone .exe

If you already have the GitHub repository mapped to your VM and want to run it without Rider (see <https://github.com/StockSharp/algotrading>):

1. Open the Windows **Command Prompt** or **PowerShell**.
2. Navigate to your StockSharp repository directory using `cd` (e.g., `cd Z:\StockSharp` depending on your mapped drive letter).
3. Run the following command to compile and publish a standalone executable:

   ```bash
   dotnet publish StockSharp.sln -c Release -r win-x64 --self-contained true
   ```

4. Once completed, navigate to the `bin/Release/.../publish` directory. You will find standard Windows executable files (`.exe`) that you can double-click to launch the UI tools directly.
