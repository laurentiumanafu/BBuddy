using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using MahApps.Metro.Controls;
using Styx.Helpers;
using Styx.Logic.Pathing;
using Styx.WoWInternals;

namespace CopilotBuddy.UI
{
    /// <summary>
    /// The combat side of the settings: heal / eat / drink percentages, the damage-over-time
    /// switch, and - on the Spells tab - what each spell on the action bar is for.
    ///
    /// It is a thin view over <see cref="ClickAimSettings"/> and <see cref="ClickAimCombat"/>:
    /// opening it loads the same files the grind thread reads, Save writes them back, and the
    /// next Start picks them up. Nothing here is authoritative - the files are - so a hand edit
    /// and this window cannot disagree.
    ///
    /// The spells list is the answer to "I learned Moonfire but the bot never casts it": every
    /// filled slot is shown with the id, the key it is bound to and the role the rotation will
    /// use. If the role says Unknown or Utility when it should be Dot, set it here.
    /// </summary>
    public partial class ClickAimCombatSettingsWindow : MetroWindow
    {
        /// <summary>True once Save has written the files, false when the user cancelled.</summary>
        public bool Saved { get; private set; }

        /// <summary>One row of the spells list. Rebuilt wholesale, so no change notification.</summary>
        public sealed class SpellRow
        {
            // Properties, not fields: the ListView columns bind to these, and WPF binding only
            // resolves properties.
            public int Id { get; set; }
            public string Slot { get; set; }
            public string Key { get; set; }
            public string Spell { get; set; }
            public string Role { get; set; }
            public string Range { get; set; }
            public string Source { get; set; }

            public SpellRow() { Slot = ""; Key = ""; Spell = ""; Role = ""; Range = ""; Source = ""; }
        }

        private readonly List<SpellRow> _rows = new List<SpellRow>();

        /// <summary>The spell roles as they were when the window opened, so Cancel can undo.</summary>
        private readonly Dictionary<int, ClickAimSpellRole> _originalRoles =
            new Dictionary<int, ClickAimSpellRole>();

        public ClickAimCombatSettingsWindow()
        {
            InitializeComponent();

            ClickAimSettings.Load();   // also loads the user's spell roles
            CaptureOriginalRoles();
            LoadFromSettings();
            LoadSpells();

            try { lblFile.Text = "Saved to " + ClickAimSettings.Path; }
            catch (Exception ex) { lblFile.Text = "Settings file: " + ex.Message; }
        }

        // ---------------------------------------------------------------- numbers

        /// <summary>Puts the current values into the boxes; called on open and by Defaults.</summary>
        private void LoadFromSettings()
        {
            txtHeal.Text = Text(ClickAimSettings.HealBelowPercent);
            txtHealMana.Text = Text(ClickAimSettings.HealMinManaPercent);
            txtEat.Text = Text(ClickAimSettings.EatBelowPercent);
            txtDrink.Text = Text(ClickAimSettings.DrinkBelowPercent);
            txtDotSeconds.Text = Text(ClickAimSettings.DotRefreshSeconds);
            txtFoodSlot.Text = Text(ClickAimSettings.FoodSlot);
            txtDrinkSlot.Text = Text(ClickAimSettings.DrinkSlot);
            txtRestMs.Text = Text(ClickAimSettings.RestMs);
            chkDot.IsChecked = ClickAimSettings.UseDamageOverTime;
            chkBuffs.IsChecked = ClickAimSettings.UseBuffs;
        }

        private static string Text(int value)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Reads the boxes into the settings object. A box that is not a number is named in the
        /// message and stops the save, because silently substituting a default for a typo is how
        /// a user ends up eating at a percentage they never chose.
        /// </summary>
        private bool ApplyToSettings()
        {
            int heal, healMana, eat, drink, dotSeconds, foodSlot, drinkSlot, restMs;
            if (!TryRead(txtHeal, "Heal at or below", out heal)) return false;
            if (!TryRead(txtHealMana, "Heal only above", out healMana)) return false;
            if (!TryRead(txtEat, "Eat at or below", out eat)) return false;
            if (!TryRead(txtDrink, "Drink at or below", out drink)) return false;
            if (!TryRead(txtDotSeconds, "Refresh seconds", out dotSeconds)) return false;
            if (!TryRead(txtFoodSlot, "Food slot", out foodSlot)) return false;
            if (!TryRead(txtDrinkSlot, "Drink slot", out drinkSlot)) return false;
            if (!TryRead(txtRestMs, "Rest time", out restMs)) return false;

            ClickAimSettings.HealBelowPercent = heal;
            ClickAimSettings.HealMinManaPercent = healMana;
            ClickAimSettings.EatBelowPercent = eat;
            ClickAimSettings.DrinkBelowPercent = drink;
            ClickAimSettings.DotRefreshSeconds = dotSeconds;
            ClickAimSettings.FoodSlot = foodSlot;
            ClickAimSettings.DrinkSlot = drinkSlot;
            ClickAimSettings.RestMs = restMs;
            ClickAimSettings.UseDamageOverTime = chkDot.IsChecked == true;
            ClickAimSettings.UseBuffs = chkBuffs.IsChecked == true;

            // Ranges are the loop's, not the form's: 900% is clamped to 100 here so the box shows
            // what will actually be used instead of keeping a number that means nothing.
            ClickAimSettings.Clamp();
            LoadFromSettings();
            return true;
        }

        private static bool TryRead(System.Windows.Controls.TextBox box, string label, out int value)
        {
            value = 0;
            string text = (box.Text ?? string.Empty).Trim();
            if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
                return true;

            System.Windows.MessageBox.Show(label + " must be a whole number (0 turns it off).",
                            "Combat settings", MessageBoxButton.OK, MessageBoxImage.Warning);
            box.Focus();
            box.SelectAll();
            return false;
        }

        // ---------------------------------------------------------------- spells

        /// <summary>
        /// Fills the list. With the client attached this is the real action bar, read the same way
        /// the rotation reads it; with nothing attached it is whatever the user set by hand, so
        /// the settings can still be reviewed and undone before the game is even running.
        /// </summary>
        private void LoadSpells()
        {
            _rows.Clear();
            int overrides = ClickAimCombat.OverrideCount;
            string note;

            List<ClickAimAction>? bar = null;
            WowBReader? reader = ObjectManager.WowB;
            if (reader != null)
            {
                try { bar = ClickAimCombat.ReadBar(reader); }
                catch (Exception ex)
                {
                    bar = null;
                    Logging.Write("[Grind] combat settings: action bar unreadable: " + ex.Message);
                }
            }

            if (bar != null)
            {
                for (int i = 0; i < bar.Count; i++)
                {
                    ClickAimAction action = bar[i];
                    if (action.Kind != ClickAimActionKind.Spell) continue;   // items are on the other tab
                    _rows.Add(RowOf(action));
                }
                note = string.Format(CultureInfo.InvariantCulture,
                    "{0} spell(s) on the bar, {1} set by hand.", _rows.Count, overrides);
            }
            else
            {
                List<int> ids = ClickAimCombat.OverrideIds();
                for (int i = 0; i < ids.Count; i++)
                {
                    var row = new SpellRow { Id = ids[i], Slot = "-", Key = "-" };
                    row.Spell = SpellName(ids[i]);   // name table only: no bar to read
                    row.Role = RoleText(ClickAimCombat.RoleOf(ids[i]));
                    row.Range = RangeText(ids[i]);
                    row.Source = "hand";
                    _rows.Add(row);
                }
                note = ids.Count == 0
                    ? "Attach to the game to see the action bar. Nothing is set by hand yet."
                    : "Not attached: showing the " + ids.Count.ToString(CultureInfo.InvariantCulture)
                      + " spell(s) set by hand.";
            }

            lstSpells.ItemsSource = null;
            lstSpells.ItemsSource = _rows;
            lblSpells.Text = note;
        }

        private static SpellRow RowOf(ClickAimAction action)
        {
            var row = new SpellRow
            {
                Id = action.Id,
                Slot = action.Slot.ToString(CultureInfo.InvariantCulture),
                Key = ClickAimCombat.KeyLabel(action.KeyVk),
                Spell = action.Name + " (" + action.Id.ToString(CultureInfo.InvariantCulture) + ")"
            };
            row.Role = RoleText(ClickAimCombat.RoleOf(action.Id));
            row.Range = RangeText(action.Id);
            row.Source = ClickAimCombat.RoleOverrideOf(action.Id) == ClickAimSpellRole.Unknown
                ? "built-in" : "hand";
            return row;
        }

        /// <summary>"Moonfire (8921)" when the name table knows the id, "spell 12345" otherwise.</summary>
        private static string SpellName(int id)
        {
            string name = ClickAimCombat.NameOf(id);
            return name + " (" + id.ToString(CultureInfo.InvariantCulture) + ")";
        }

        /// <summary>
        /// How far the spell reaches, in the words that matter: "melee" for the 5 yd kit, the
        /// number otherwise. This is the column that answers "the bot thinks everything is
        /// ranged" - a Claw reading "melee 5 yd" is a promise the rotation will walk in first.
        /// </summary>
        private static string RangeText(int id)
        {
            double range = ClickAimCombat.RangeOf(id);
            if (range <= 0.0) return "?";
            return ClickAimCombat.IsMeleeSpell(id)
                ? "melee " + range.ToString("F0", CultureInfo.InvariantCulture) + " yd"
                : range.ToString("F0", CultureInfo.InvariantCulture) + " yd";
        }

        private static string RoleText(ClickAimSpellRole role)
        {
            return role == ClickAimSpellRole.Unknown ? "unknown" : role.ToString();
        }

        private void lstSpells_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var row = lstSpells.SelectedItem as SpellRow;
            if (row == null) return;

            ClickAimSpellRole role = ClickAimCombat.RoleOverrideOf(row.Id);
            cmbRole.SelectedIndex = IndexOfRole(role);
        }

        private static int IndexOfRole(ClickAimSpellRole role)
        {
            switch (role)
            {
                case ClickAimSpellRole.Damage: return 1;
                case ClickAimSpellRole.Dot: return 2;
                case ClickAimSpellRole.Heal: return 3;
                case ClickAimSpellRole.Buff: return 4;
                case ClickAimSpellRole.Attack: return 5;
                case ClickAimSpellRole.Utility: return 6;
                case ClickAimSpellRole.Ignore: return 7;
                default: return 0;
            }
        }

        private static ClickAimSpellRole RoleAtIndex(int index)
        {
            switch (index)
            {
                case 1: return ClickAimSpellRole.Damage;
                case 2: return ClickAimSpellRole.Dot;
                case 3: return ClickAimSpellRole.Heal;
                case 4: return ClickAimSpellRole.Buff;
                case 5: return ClickAimSpellRole.Attack;
                case 6: return ClickAimSpellRole.Utility;
                case 7: return ClickAimSpellRole.Ignore;
                default: return ClickAimSpellRole.Unknown;   // "(built-in)"
            }
        }

        private void btnApplyRole_Click(object sender, RoutedEventArgs e)
        {
            var row = lstSpells.SelectedItem as SpellRow;
            if (row == null)
            {
                System.Windows.MessageBox.Show("Pick a spell in the list first.",
                                "Combat settings", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            ClickAimCombat.SetRoleOverride(row.Id, RoleAtIndex(cmbRole.SelectedIndex));
            LoadSpells();
        }

        private void btnRefreshSpells_Click(object sender, RoutedEventArgs e)
        {
            LoadSpells();
        }

        // ---------------------------------------------------------------- buttons

        private void btnDefaults_Click(object sender, RoutedEventArgs e)
        {
            ClickAimSettings.ResetDefaults();
            LoadFromSettings();
        }

        private void btnSave_Click(object sender, RoutedEventArgs e)
        {
            if (!ApplyToSettings()) return;

            bool numbers = ClickAimSettings.Save();
            bool roles = ClickAimCombat.SaveRoleOverrides();
            if (!numbers || !roles)
            {
                System.Windows.MessageBox.Show("The settings files could not be written. See the log for the path.",
                                "Combat settings", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            Saved = true;
            Logging.Write("[Grind] combat settings saved: " + ClickAimSettings.Describe());
            Close();
        }

        private void btnCancel_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        /// <summary>
        /// Applying a role changes the live table immediately, so Cancel has to put it back:
        /// otherwise a user who tried an override, did not like it and pressed Cancel would still
        /// get it on the next Start.
        /// </summary>
        protected override void OnClosed(EventArgs e)
        {
            if (!Saved)
            {
                ClickAimCombat.ClearRoleOverrides();
                foreach (KeyValuePair<int, ClickAimSpellRole> pair in _originalRoles)
                    ClickAimCombat.SetRoleOverride(pair.Key, pair.Value);
            }
            base.OnClosed(e);
        }

        private void CaptureOriginalRoles()
        {
            _originalRoles.Clear();
            List<int> ids = ClickAimCombat.OverrideIds();
            for (int i = 0; i < ids.Count; i++)
                _originalRoles[ids[i]] = ClickAimCombat.RoleOverrideOf(ids[i]);
        }
    }
}
