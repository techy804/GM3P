# NOTICE
GM3P has been canceled. Its development will no longer continue.<br />
Usage of the program is, of course, still possible. However, we recommend different tools in order to create the best possible experience for you.

# GameMaker Mass Mod Patcher (GM3P)

**G**ame**M**aker **M**ass **M**od **P**atcher (abbreviated to **GM3P**) is a tool used to merge multiple xdelta mods for GameMaker games and thus be able to play multiple mods at once.<br />

_This tool was used in the backend of [Deltamod](https://gamebanana.com/tools/20575) for mod merging. However, it has been replaced._

## How to build
1. Make sure you have .NET 8.0 or later and Git installed<br />
2. Open the .sln up in VS or Jetbrains and build the project "GM3P". (IDK about other IDEs like VSCode)<br />
3. Open the .sln up of [UTMT-For-GM3P](https://github.com/deltamodders/UTMT-For-GM3P) and build the project "UnderTaleModCli"<br />
4. Copy and paste the UTMT build into the GM3P build under a folder named "UTMTCLI" <br />
5. Copy and paste the contents of the "UTMT Scripts" folder under a folder named "Scripts" under "UTMTCLI" <br />
6. Download [xDelta3 v3.0.11 64-bit](https://github.com/jmacd/xdelta-gpl/releases/download/v3.0.11/xdelta3-3.0.11-x86_64.exe.zip) and paste it under the GM3P build.
7. Happy patching

## Usage
There are 2 versions of the tool on the same executable: *Console* and *CLI*. You can enter the console by either entering the command `console` or just launching the executable without any commands. You can use the CLI version by using various commands. We try to keep parity in both versions as much as possible, but some features or options are locked to one version or the other. For example, entering in your own fork of UTMTCLI is only available on the console app, and changing your output path is only available on the CLI version.

### Console

By default, GM3P will launch the "Console" app, which is an on-rails way to go through patching your game without having to know much about commands. Mostly, just follow the on-screen instructions, but there are a couple of common misunderstandings with the wording. The most common misunderstanding is when it asks the number of mods in a chapter, it is asking what the maximum number of mods you want to apply in a single chapter.

### CLI

The CLI version was made for advanced users who want more control over the process and for toolmakers who want to use GM3P in their own tools. There are 6 commands in the CLI version:

`config` - Changes settings and saves them in a JSON format Example: `GM3P.exe config update c.enablefastcombiner false save ./config.json`

`massPatch` - The titular command. Makes multiple copies of the game, then patches each copy with a unique mod. The `compare` and `result` commands require this command to be called first. Otherwise, they will fail.
Example: `GM3P.exe massPatch "./myGameMaker Game" GM 2 "./myMod1.xDelta","./MyMod2.csx"`

`compare` - Compares and Combines modified objects
Example: `GM3P.exe compare 2 true false`

`result` - Creates modpack
Example: `GM3P.exe result "My Modpack" true 2`

`clear` - clears temporary files; I recommend using it either before or after every session

`console` - Launches the console version of the tool

And last, but not least, `help`, which gives more detailed information on each of these commands.

## Credits
| | Name | Role |
|-|------|-------|
| <img src="./GitHubAssets/techy.png" alt="Techy" width="60" height="60"> | **[techy804](https://gamebanana.com/members/4548254)** | Creator of GM3P |
| <img src="./GitHubAssets/zorkats.jpeg" alt="Zorkats" width="60" height="60"> | **[Zorkats](https://gamebanana.com/members/3914910)** | Former programmer |

## License
Licensed under GNU GPL 3.0
