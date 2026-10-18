using OverTheHillPlus.UI;

namespace OverTheHillPlus.CarScale;

/// <summary>Car Scale's MODS page: the host picks who is mini or giant; a guest can ask for its own size.</summary>
internal static class CarScaleSimple
{
    internal static void Draw()
    {
        // Not bound when the mod failed to start: nothing to show then.
        if (CarScaleConfig.Mode == null) return;
        bool inWorld = GameState.Runner != null;
        bool host = !inWorld || GameState.IsAuthority;

        // A private mod is never listed in the session: whether the host runs it shows on the mod network.
        if (inWorld && !host && !CarScaleCore.HostHasIt)
            SimpleMenu.Note("This session's host doesn't run Car Scale: every car keeps its size.");

        if (host) DrawHost(inWorld);
        else DrawGuest();
        SimpleMenu.Note("Everyone in the session needs Car Scale to see the sizes; players without it see normal cars. " +
                        "Achievements stay locked for the session once a car is resized.");
    }

    private static void DrawHost(bool inWorld)
    {
        SimpleMenu.Heading("Who");
        var mode = CarScaleConfig.Mode.Value;
        SimpleMenu.Choice("Resize", "Just your car, every player's car, or a size per player (list below).",
            (int)mode, ScaleMath.ModeLabels, i => CarScaleConfig.Mode.Value = (ScaleMath.Mode)i, wrap: false);
        bool sized = mode == ScaleMath.Mode.JustMe || mode == ScaleMath.Mode.Everyone;
        SimpleMenu.FloatChoice("Size", "Mini below 1, giant above. The weight, tyres, suspension and brakes follow.",
            CarScaleConfig.Size.Value, ScaleMath.Steps, ScaleMath.Label, v => CarScaleConfig.Size.Value = v, sized);
        SimpleMenu.Toggle(CarScaleConfig.AllowRequests, "Let players choose", "Players with Car Scale may pick their own size (not in Everyone).");

        if (!inWorld) return;
        var rows = CarScaleCore.Rows();
        SimpleMenu.Heading($"Players ({rows.Count})");
        bool perPlayer = mode == ScaleMath.Mode.PerPlayer;
        foreach (var r in rows)
        {
            string who = r.IsMe ? r.Name + " (you)" : r.Name;
            string asked = r.Asked.HasValue && CarScaleConfig.AllowRequests.Value ? $" · asks {ScaleMath.Label(r.Asked.Value)}" : "";
            if (perPlayer)
            {
                int id = r.Id;
                float current = r.Pick ?? r.Size;
                SimpleMenu.FloatChoice(who + asked, "This player's size (this session).", current, ScaleMath.Steps, ScaleMath.Label, v =>
                {
                    if (ScaleMath.IsNormal(v)) CarScaleCore.PerPlayer.Remove(id);
                    else CarScaleCore.PerPlayer[id] = ScaleMath.Clamp(v);
                });
            }
            else SimpleMenu.Button(who + asked, ScaleMath.Label(r.Size), "Pick sizes per player with Resize = Per player.", null, false);
        }
        if (perPlayer && CarScaleCore.PerPlayer.Count > 0)
            SimpleMenu.Button("Everyone back to normal", "", "Clears the per-player sizes.", () => CarScaleCore.PerPlayer.Clear());
    }

    private static void DrawGuest()
    {
        SimpleMenu.Heading("Your car");
        bool canAsk = CarScaleCore.HostHasIt && !GameState.IsPublicGuest;
        SimpleMenu.FloatChoice("Ask for size", "Asks the host; it applies when the host lets players choose.",
            CarScaleConfig.MySize.Value, ScaleMath.Steps, ScaleMath.Label, v =>
            {
                CarScaleConfig.MySize.Value = v;
                CarScaleCore.RequestSoon();
            }, canAsk);
        SimpleMenu.Note($"Your car now: {ScaleMath.Label(CarScaleCore.MySize)}.");
        if (GameState.IsPublicGuest) SimpleMenu.Note("Not in public lobbies: your size changes what everyone sees.");
    }

    /// <summary>The Advanced fold.</summary>
    internal static void DrawAdvanced()
    {
        if (CarScaleConfig.Mode == null) return;
        Menu.Card("Physics", "What follows the size on the host", () =>
        {
            Menu.Switch(CarScaleConfig.ScaleSuspension, "Scale the suspension",
                "Suspension travel and damping follow the size (tyres, weight and brakes always do)", true);
            Menu.Small($"Sizes go from {ScaleMath.Min:0.#}× to {ScaleMath.Max:0.#}×: beyond that the car physics stops behaving.");
        });
    }
}
