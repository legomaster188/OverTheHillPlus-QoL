using System;
using System.Collections.Generic;

namespace OverTheHillPlus.Api;

public static partial class Career
{
    /// <summary>
    /// Jobs and quests: data plus small handlers that mods register (<see cref="JobTemplate"/>). The career mod puts their
    /// offers on its boards, runs a taken job's stages (pick up, deliver, reach, haul N loads, timed, custom) and pays when it
    /// is done. In a session the host runs every job from what it sees (the cars' real positions); guests only ask. Every step
    /// is an event (<see cref="Changed"/> and the template's own <see cref="JobTemplate.Changed"/>).
    /// </summary>
    public static class Jobs
    {
        public const int MaxStages = 24, MaxLoads = 50, MaxTemplates = 256;
        /// <summary>The most one job may pay (before the multiplier) and the longest it may take, seconds.</summary>
        public const long MaxPay = 250_000;
        public const float MaxTime = 4f * 3600f;

        private static readonly Dictionary<string, JobTemplate> ById = new(StringComparer.Ordinal);
        private static readonly List<JobTemplate> Ordered = new();

        /// <summary>Every step of every job this game knows about (on the host: everyone's; on a guest: its own).</summary>
        public static event Action<JobEvent> Changed;

        /// <summary>
        /// Registers (or, for your own id, replaces) a kind of job. False (and logged) for a bad mod id or template id, no
        /// <see cref="JobTemplate.Make"/>, an unknown board, or an id another mod registered. <paramref name="owner"/> is your
        /// mod id; the template is kept as you made it (its handlers are called as they are).
        /// </summary>
        public static bool Register(string owner, JobTemplate template)
        {
            string problem = TemplateProblem(owner, template);
            if (problem != null)
            {
                Warn("job " + owner + "/" + template?.Id, $"Jobs.Register from {owner}: {problem}");
                return false;
            }
            template.Owner = owner;
            template.Weight = Math.Clamp(template.Weight, 1, 100);
            template.Name = Clean(template.Name, 48);
            if (ById.TryGetValue(template.Id, out var old)) Ordered.Remove(old);
            ById[template.Id] = template;
            Ordered.Add(template);
            return true;
        }

        private static string TemplateProblem(string owner, JobTemplate t)
        {
            if (!IsId(owner)) return "the mod id is not valid";
            if (t == null || !IsId(t.Id, 60)) return "the template id is not valid (2-60 of a-z, 0-9, '_', '.', '-')";
            if (t.Make == null) return "it has no Make";
            if (Array.IndexOf(Boards.All, t.Board) < 0) return $"\"{t.Board}\" is not a board ({string.Join(", ", Boards.All)})";
            if (ById.TryGetValue(t.Id, out var had) && !string.Equals(had.Owner, owner, StringComparison.Ordinal)) return $"{t.Id} belongs to {had.Owner}";
            if (!ById.ContainsKey(t.Id) && ById.Count >= MaxTemplates) return "there are too many job templates";
            return null;
        }

        /// <summary>Takes a template of yours off (its offers leave the boards; a job of it being done runs to its end).</summary>
        public static bool Unregister(string owner, string id)
        {
            if (id == null || !ById.TryGetValue(id, out var t) || !string.Equals(t.Owner, owner, StringComparison.Ordinal)) return false;
            ById.Remove(id);
            Ordered.Remove(t);
            return true;
        }

        /// <summary>Every template, in the order they were registered.</summary>
        public static IReadOnlyList<JobTemplate> Templates => Ordered;

        public static JobTemplate Find(string id) => id != null && ById.TryGetValue(id, out var t) ? t : null;

        /// <summary>The templates of one board.</summary>
        public static List<JobTemplate> For(string board)
        {
            var l = new List<JobTemplate>();
            foreach (var t in Ordered) if (string.Equals(t.Board, board, StringComparison.Ordinal)) l.Add(t);
            return l;
        }

        /// <summary>The job this game's player is doing, or null.</summary>
        public static JobView Current
        {
            get
            {
                try { return Driver?.CurrentJob; }
                catch (Exception e) { DebugLog.Error("core", e, "Career.Jobs.Current"); return null; }
            }
        }

        /// <summary>The offers on a board this game's player can see now.</summary>
        public static IReadOnlyList<JobView> Offers(string board)
        {
            try { return Driver?.Offers(board) ?? (IReadOnlyList<JobView>)Array.Empty<JobView>(); }
            catch (Exception e) { DebugLog.Error("core", e, "Career.Jobs.Offers"); return Array.Empty<JobView>(); }
        }

        /// <summary>Takes an offer (<see cref="JobView.OfferId"/>); a guest asks the host. False with why.</summary>
        public static bool Accept(string offerId, out string why)
        {
            why = null;
            if (!Playing) { why = "No career is playing."; return false; }
            if (Driver == null) { why = "The career mod isn't running."; return false; }
            try { return Driver.Accept(offerId, out why); }
            catch (Exception e) { DebugLog.Error("core", e, "Career.Jobs.Accept"); why = e.Message; return false; }
        }

        /// <summary>Gives up the current job. False with why.</summary>
        public static bool Abandon(out string why)
        {
            why = null;
            if (Driver == null) { why = "The career mod isn't running."; return false; }
            try { return Driver.Abandon(out why); }
            catch (Exception e) { DebugLog.Error("core", e, "Career.Jobs.Abandon"); why = e.Message; return false; }
        }

        /// <summary>True when the career did a job of this template that isn't repeatable (a quest): it isn't offered again.</summary>
        public static bool IsDone(string templateId) => Book != null && templateId != null && Book.DoneOnce.Contains(templateId);

        /// <summary>The templates <see cref="IsDone"/> says yes to (empty without a career).</summary>
        public static IReadOnlyCollection<string> DoneOnce => Book != null ? Book.DoneOnce : Array.Empty<string>();

        // ---------------------------------------------------------------- offers

        /// <summary>Why an offer can't go on a board, or null when it can (the career checks every offer a template makes).</summary>
        public static string Problem(JobOffer offer)
        {
            if (offer == null) return "no offer";
            if (offer.Stages == null || offer.Stages.Count == 0) return "it has no stages";
            if (offer.Stages.Count > MaxStages) return $"it has more than {MaxStages} stages";
            if (offer.Pay < 0 || offer.Pay > MaxPay || offer.BonusPay < 0 || offer.BonusPay > MaxPay) return "its pay is out of range";
            if (!Time(offer.TimeLimit) || !Time(offer.BonusTime)) return "a time is out of range";
            for (int i = 0; i < offer.Stages.Count; i++)
            {
                var s = offer.Stages[i];
                if (s == null) return $"stage {i + 1} is missing";
                if (!Enum.IsDefined(typeof(StageKind), s.Kind)) return $"stage {i + 1} has an unknown kind";
                if (s.Kind != StageKind.Custom && !s.At.IsValid) return $"stage {i + 1} has no valid place";
                if (s.Kind == StageKind.Haul && (!s.From.IsValid || s.Loads < 1 || s.Loads > MaxLoads)) return $"stage {i + 1}: a haul needs a pick-up and 1 to {MaxLoads} loads";
                if (s.Kind == StageKind.Timed && !(s.Seconds > 0f && s.Seconds <= 3600f)) return $"stage {i + 1}: a timed stage needs 1 s to 1 h";
                if (!Time(s.TimeLimit)) return $"stage {i + 1}: its time limit is out of range";
                if (!NeedOk(s.Need)) return $"stage {i + 1}: \"{s.Need}\" is not a need this career knows";
            }
            return null;
        }

        private static bool Time(float t) => float.IsFinite(t) && t >= 0f && t <= MaxTime;

        /// <summary>"", "towing", "towing:&lt;kind&gt;", "empty" or "custom:&lt;key&gt;".</summary>
        internal static bool NeedOk(string need)
        {
            if (string.IsNullOrEmpty(need) || need == "towing" || need == "empty") return true;
            if (need.StartsWith("towing:", StringComparison.Ordinal)) return IsId(need.Substring(7), 24);
            if (need.StartsWith("custom:", StringComparison.Ordinal)) return IsId(need.Substring(7), 40);
            return false;
        }

        /// <summary>
        /// The trailer a stage's need asks for: "utility", "camper", "hauler", "boat", "semi", or "flatbed" (the car hauler, or
        /// the semi-trailer behind a semi truck); null when it needs none. A plain "towing" takes any: the light one is asked for.
        /// </summary>
        public static string TrailerFor(string need)
        {
            if (string.IsNullOrEmpty(need)) return null;
            if (need == "towing") return "utility";
            if (need.StartsWith("towing:", StringComparison.Ordinal)) return need.Substring(7);
            if (need == "custom:flatbed") return "flatbed";
            if (need == "custom:semi") return "semi";
            return null;
        }

        /// <summary>The trailer the job needs next: the current stage's, else the next stage's that asks for one (the depot hands
        /// it out before the driver gets there). Null: none.</summary>
        public static string TrailerNext(JobView job)
        {
            if (job?.Stages == null) return null;
            for (int i = Math.Max(0, job.Stage); i < job.Stages.Count; i++)
            {
                string k = TrailerFor(job.Stages[i].Need);
                if (k != null) return k;
            }
            return null;
        }

        /// <summary>True when a car towing <paramref name="towing"/> (a kind, null: nothing) meets the trailer part of a need (a wrecker meets the car hauler).</summary>
        public static bool TowingOk(string need, string towing, bool wrecker = false)
        {
            string want = TrailerFor(need);
            if (want == null) return need != "empty" || towing == null;
            if (wrecker && want == "hauler") return true;
            if (need == "towing") return towing != null;
            if (want == "flatbed") return towing == "hauler" || towing == "semi";
            return string.Equals(want, towing, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>What a need asks for, in a few plain words ("the car hauler"); null when it asks for nothing a player can see.</summary>
        public static string NeedText(string need) => need switch
        {
            "towing" => "a trailer",
            "empty" => "no trailer",
            "towing:utility" => "the utility trailer",
            "towing:camper" => "the camper",
            "towing:hauler" => "the car hauler",
            "towing:boat" => "the boat trailer",
            "towing:semi" or "custom:semi" => "a semi truck with the flatbed",
            "custom:flatbed" => "a flatbed (the car hauler will do)",
            "custom:plow" => "the plow on your car",
            _ => null,
        };

        /// <summary>A checked copy of an offer for a board (texts cleaned and cut), or null with <paramref name="why"/>.</summary>
        internal static JobOffer Sanitize(JobOffer offer, JobTemplate t, out string why)
        {
            why = Problem(offer);
            if (why != null) return null;
            var c = new JobOffer
            {
                Title = Clean(string.IsNullOrWhiteSpace(offer.Title) ? t?.Name : offer.Title, 48),
                Description = Clean(offer.Description, 240),
                Pay = offer.Pay, BonusPay = offer.BonusPay, BonusTime = offer.BonusTime, TimeLimit = offer.TimeLimit,
                Data = offer.Data != null && offer.Data.Length > CareerBook.MaxText ? offer.Data.Substring(0, CareerBook.MaxText) : offer.Data,
            };
            if (offer.Unlocks != null)
            {
                var tags = new List<string>();
                foreach (var u in offer.Unlocks) if (IsId(u)) tags.Add(u);
                c.Unlocks = tags.ToArray();
            }
            foreach (var s in offer.Stages)
            {
                var sc = s.Copy();
                sc.Label = Clean(s.Label, 80);
                sc.Cargo = Clean(s.Cargo, 40);
                sc.Need ??= "";
                c.Stages.Add(sc);
            }
            if (string.IsNullOrEmpty(c.Title)) c.Title = "Job";
            return c;
        }

        /// <summary>The pay for an offer done in <paramref name="elapsed"/> seconds: its pay, its bonus when within the bonus
        /// time, times <paramref name="multiplier"/>, at most <see cref="MaxPay"/> times the multiplier.</summary>
        public static long PayFor(JobOffer offer, float elapsed, float multiplier)
        {
            if (offer == null) return 0;
            double m = float.IsFinite(multiplier) ? Math.Clamp(multiplier, CareerRuleSet.MinMultiplier, CareerRuleSet.MaxMultiplier) : 1.0;
            long pay = Math.Clamp(offer.Pay, 0, MaxPay);
            if (offer.BonusPay > 0 && offer.BonusTime > 0f && float.IsFinite(elapsed) && elapsed >= 0f && elapsed <= offer.BonusTime)
                pay += Math.Clamp(offer.BonusPay, 0, MaxPay);
            return (long)Math.Round(Math.Min(pay, MaxPay) * m);
        }

        // ---------------------------------------------------------------- the career mod

        /// <summary>The career mod: a step of a job happened. The template and every subscriber hear of it.</summary>
        internal static void Report(JobEvent e)
        {
            if (e == null) return;
            var t = Find(e.TemplateId);
            if (t?.Changed != null)
            {
                try { t.Changed(e); }
                catch (Exception ex)
                {
                    DebugLog.Error("core", ex, "Career.Jobs template " + t.Id);
                    Warn("job-event " + t.Id, $"the job template {t.Id} ({t.Owner}) threw on {e.Kind}: {ex.Message}");
                }
            }
            Career.Raise("Jobs.Changed", Changed, h => h(e));
        }

        /// <summary>The career mod: the job the career is on, as text, kept with the career so it survives leaving the game.</summary>
        internal static string SavedRun
        {
            get => Book?.ActiveJob;
            set
            {
                if (Book == null || string.Equals(Book.ActiveJob, value, StringComparison.Ordinal)) return;
                Book.ActiveJob = value;
                Touch();
            }
        }

        /// <summary>The career mod: the loaded career finished a job of <paramref name="templateId"/> (a quest is then done for good).</summary>
        internal static void NoteDone(string templateId)
        {
            if (Book == null) return;
            Book.JobsDone++;
            var t = Find(templateId);
            if (t != null && !t.Repeatable && Book.DoneOnce.Count < CareerBook.MaxDoneOnce) Book.DoneOnce.Add(t.Id);
            Touch();
        }

        /// <summary>The career mod: the loaded career failed or gave up a job.</summary>
        internal static void NoteFailed()
        {
            if (Book == null) return;
            Book.JobsFailed++;
            Touch();
        }

        internal static void Reset()
        {
            ById.Clear();
            Ordered.Clear();
            Changed = null;
        }
    }
}
