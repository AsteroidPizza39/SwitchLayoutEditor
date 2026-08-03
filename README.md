# SwitchLayoutEditor
This program can edit and render BFLYT and BFLAN files commonly used for layouts in Switch interfaces and games. It enables you to easily create/edit themes. As this is a work in progress, builds may get outdated quickly, you should check this page often.

## Usage / Wiki
Just launch the exe and open a SZS/BFLYT/BFLAN file. \
BFLYT/BFLAN files are commonly found in SZS archives, when opening a SZS file you can double click on the files in the list to edit them (if they're supported). \
For a more in depth guide, [click here](https://github.com/FuryBaguette/SwitchLayoutEditor/wiki) to go to the wiki.

## Features
- Layout loading, editing and saving
- Rendering the bounding boxes of the components
- SZS editing
- Drag and drop
- Simultaneous file editing
- Import/Export JSON patch (Compatible with Switch themes)
- Animations editing

## Support
- Use the github issues to report problems/bugs **OR**
- Join the [discord server](https://discord.gg/ap6yfR2) for support, news/announcements before anyone, be a tester or just talk.

## Screenshot
This is using the original from the Switch's home menu:
![](https://github.com/FuryBaguette/SwitchLayoutEditor/blob/master/Screenshots/MainMenu.png)

Example of a custom layout:
![](https://github.com/FuryBaguette/SwitchLayoutEditor/blob/master/Screenshots/Example.png)

## Building
To build you need these sibling repositories checked out next to this repo:

- SwitchThemesCommon shared project from [exelix11/SwitchThemeInjector](https://github.com/exelix11/SwitchThemeInjector)
- [KillzXGaming/Switch-Toolbox](https://github.com/KillzXGaming/Switch-Toolbox) (BNTX decode for pane texture preview)

From the repo root (PowerShell), run:

```powershell
.\build.ps1
.\build.ps1 -Run
```

The script clones those siblings if missing, restores NuGet packages, and builds with MSBuild. Useful flags: `-Configuration Debug`, `-SkipRestore`, `-UpdateCommon`.

You can also open `SwitchLayoutEditor.sln` in Visual Studio. In case of issues with SwitchThemesCommon, try using a version from a commit from the same day as the last commit on this repo.

## Credits
- [FuryBaguette](https://github.com/FuryBaguette) - Development
- [exelix](https://github.com/exelix11) - Base of the editor & Continuous development
- [Aboud](https://github.com/aboood40091) - [Sarc Tool](https://github.com/aboood40091/SARC-Tool)
- [Syroot](https://gitlab.com/Syroot) - [Binary Data](https://gitlab.com/Syroot/BinaryData)
