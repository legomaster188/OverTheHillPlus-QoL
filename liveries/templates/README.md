# Make your own livery

Never made one before? No problem. This takes about ten minutes.

You can also skip the editor and paint right in your browser with the web livery maker:
https://oth-livery-maker.legomaster188.workers.dev (nothing gets uploaded, it all stays on your computer).

## The five steps

1. **Pick your car** from the table below and grab its files from this folder.
2. **Open the template** in any image editor (Photoshop, GIMP, Krita, Paint.NET, even the web maker above). It's the
   car's skin laid out flat, like a cardboard box that's been unfolded. The labels tell you which bit is which: LEFT
   SIDE, RIGHT SIDE, ROOF + HOOD, FRONT, BACK and SMALL PARTS. The arrows show which way the car is facing.
3. **Paint** on a new layer above it. Colours in the template, so you know where to go:
   - cream = the body paint, paint here
   - grey-blue = trim like bumpers and mouldings, paint here too
   - blue stripes = glass, leave it alone
   - orange = lamps, leave them alone
   - dark = not on the car, ignore it

   Anything you leave empty (transparent) keeps whatever colour the player picks in the garage. Anything you fill in
   replaces the paint. Hide or delete the template layer before you save.
4. **Save it as a PNG**, 2048 x 2048 pixels (don't resize it), with transparency kept.
5. **Put it in a folder** with a file called `manifest.json` (copy the one in `example`, see below), and drop the whole
   folder into `Documents\My Games\over the hill\LocalLiveries\`. In the garage open the Livery Mods tab and turn on
   "Show local mods". It also shows up on the car's page in MODS, under Skin.

## The three files for each car

| File | What it is |
| --- | --- |
| `<car>.png` | The template with big labels, arrows and a little legend. Start here. |
| `<car>_guides.png` | Just the lines and labels on a see-through background. Lay it over your own work on its own layer. |
| `<car>_base.png` | The car's normal paint. Paint on top of this if you want to see what you're changing. |

## Which file is which car

The `car` name has to match exactly, capital letters and all, or the game won't find the car.

| Car | Template file | `car` in manifest.json |
| --- | --- | --- |
| Vera Crossover | `vera.png` | `Vera` |
| Terrier 4WD Wagon | `terrier.png` | `Terrier` |
| Carpati 24 | `carpati.png` | `Carpati` |
| Hilo 89 | `hilo.png` | `Hilo` |
| Hilo 22 | `hilo22.png` | `Hilo22` |
| Bastion 200 | `bastion.png` | `Bastion` |
| Pixie | `pixie.png` | `Pixie` |
| Bento | `bento.png` | `Bento` |
| Nomad | `nomad.png` | `Nomad` |
| Mule | `mule.png` | `Mule` |
| Longhorn 389 | `longhorn.png` | `Longhorn` |
| Ibex | `ibex.png` | `Ibex` |
| Marmot | `marmot.png` | `Marmot` |
| Drover | `drover.png` | `Drover` |
| Limber | `limber.png` | `Limber` |
| Badger | `badger.png` | `Badger` |
| Gemse 6x6 | `gemse.png` | `Gemse` |

(Every car's other two files are named the same with `_guides` or `_base` before `.png`, for example `hilo_guides.png`.)

## The manifest

`example/manifest.json` is ready to copy. Change `car` to your car, `name` to whatever you want to call your livery, and
make `texture` and `icon` match your file names:

```json
{"liveries": [{"car": "Hilo", "name": "My Livery", "texture": "my_livery.png", "icon": "my_livery_icon.png"}]}
```

`icon` is a small picture for the garage list (256 x 256 is plenty). A screenshot of your car works great. You can put
several liveries in one manifest, one entry each.

## Good to know

- Everyone in the lobby needs the same car mods as the host.
- Other players only see your livery if they have it too. Sharing it on mod.io works like any other livery: put the car's
  name in the title or description so people can find it.
