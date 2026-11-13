using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SWTool
{
    /// <summary>Lists the connection profiles (APNs) stored in the modem and lets the user add, change and delete them.</summary>
    internal sealed class ApnDialog : Form
    {
        private readonly MainForm main;
        private readonly Modem modem;
        private readonly BufferedListView list;
        private readonly Button addButton, editButton, deleteButton;
        private List<ApnProfile> profiles = new List<ApnProfile>();
        private bool working;

        public ApnDialog(MainForm main, Modem modem)
        {
            this.main = main;
            this.modem = modem;

            Text = "APN profiles";
            Font = main.Font;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleDimensions = new SizeF(96f, 96f);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(600, 360);

            Label hint = new Label();
            hint.Text = "Connection profiles stored in the modem. Profile 1 is normally the one used to attach to LTE: "
                + "an APN there that does not belong to the SIM card keeps the modem off LTE.";
            hint.SetBounds(12, 10, 576, 34);

            list = new BufferedListView();
            list.Dock = DockStyle.None;
            list.MultiSelect = false;
            list.SetBounds(12, 50, 476, 244);
            list.Columns.Add("No.", 40);
            list.Columns.Add("APN", 190);
            list.Columns.Add("IP type", 70);
            list.Columns.Add("Sign-in", 100);
            list.Columns.Add("State", 70);
            list.SelectedIndexChanged += delegate { UpdateButtons(); };
            list.DoubleClick += delegate { OnEdit(); };

            addButton = MakeButton("Add...", 50, delegate { OnAdd(); });
            editButton = MakeButton("Edit...", 82, delegate { OnEdit(); });
            deleteButton = MakeButton("Delete", 114, delegate { OnDelete(); });
            Button close = MakeButton("Close", 268, null);
            close.DialogResult = DialogResult.Cancel;
            CancelButton = close;

            Label note = new Label();
            note.ForeColor = Color.FromArgb(0x55, 0x55, 0x55);
            note.Text = "Changes are stored in the modem at once. It uses a changed profile the next time it attaches, "
                + "so register again or restart the modem to apply it now. Windows may set up profiles of its own when it connects.";
            note.SetBounds(12, 302, 576, 50);

            Controls.Add(hint);
            Controls.Add(list);
            Controls.Add(addButton);
            Controls.Add(editButton);
            Controls.Add(deleteButton);
            Controls.Add(close);
            Controls.Add(note);
            UpdateButtons();
        }

        private Button MakeButton(string text, int top, EventHandler click)
        {
            Button b = new Button();
            b.Text = text;
            b.SetBounds(498, top, 90, 26);
            if (click != null) b.Click += click;
            return b;
        }

        protected override async void OnShown(EventArgs e)
        {
            base.OnShown(e);
            await Reload();
        }

        private ApnProfile Selected
        {
            get { return list.SelectedItems.Count == 1 ? list.SelectedItems[0].Tag as ApnProfile : null; }
        }

        private void UpdateButtons()
        {
            ApnProfile p = Selected;
            addButton.Enabled = !working && FreeIds().Count > 0;
            editButton.Enabled = !working && p != null;
            deleteButton.Enabled = !working && p != null;
        }

        /// <summary>Runs a modem operation with this dialog's buttons disabled. Returns false if it could not be done.</summary>
        private async Task<bool> Run(string status, Func<AtResponse> operation, string failureText)
        {
            working = true;
            UpdateButtons();
            try
            {
                AtResponse r = await main.RunBusy(status, operation);
                if (r != null && !r.Ok)
                {
                    string reason = r.Busy ? "The modem is still busy with an earlier command."
                        : r.TimedOut ? "The modem did not answer in time."
                        : "Modem response: " + r.Result;
                    MessageBox.Show(this, failureText + "\n\n" + reason
                        + (r.Result.IndexOf("ERROR", StringComparison.Ordinal) >= 0
                            ? "\n\nA profile that is in use by a data connection cannot be changed; disconnect first." : ""),
                        "APN profiles", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                // The port went away under the command; the main window deals with that.
                main.ModemFailed(ex);
                DialogResult = DialogResult.Cancel;
                return false;
            }
            finally
            {
                working = false;
                if (!IsDisposed) UpdateButtons();
            }
        }

        private async Task Reload()
        {
            List<ApnProfile> read = null;
            string problem = null;
            working = true;
            UpdateButtons();
            try
            {
                read = await main.RunBusy("Reading APN profiles...", delegate { return modem.ReadApnProfiles(); });
            }
            catch (InvalidOperationException ex)
            {
                problem = ex.Message;
            }
            catch (Exception ex)
            {
                main.ModemFailed(ex);
                DialogResult = DialogResult.Cancel;
                return;
            }
            finally
            {
                working = false;
            }
            if (IsDisposed) return;
            if (problem != null) MessageBox.Show(this, problem, "APN profiles", MessageBoxButtons.OK, MessageBoxIcon.Warning);

            int keep = Selected != null ? Selected.Id : -1;
            profiles = read ?? new List<ApnProfile>();
            list.Items.Clear();
            foreach (ApnProfile p in profiles)
            {
                string signIn = p.Auth < 0 ? "" : p.Auth == 0 ? "None"
                    : ApnProfile.AuthName(p.Auth) + (p.User.Length > 0 ? ", " + p.User : "");
                ListViewItem it = new ListViewItem(new[]
                {
                    p.Id.ToString(CultureInfo.InvariantCulture), p.Apn.Length > 0 ? p.Apn : "(network default)",
                    p.Type, signIn, p.Active ? "In use" : ""
                });
                it.Tag = p;
                it.Selected = p.Id == keep;
                list.Items.Add(it);
            }
            UpdateButtons();
        }

        /// <summary>Profile numbers that are not taken yet, lowest first (at most a handful is ever needed).</summary>
        private List<int> FreeIds()
        {
            List<int> free = new List<int>();
            foreach (int id in modem.ApnIds)
            {
                bool used = false;
                foreach (ApnProfile p in profiles) used |= p.Id == id;
                if (!used) free.Add(id);
                if (free.Count >= 16) break;
            }
            return free;
        }

        private async void OnAdd()
        {
            if (working) return;
            ApnProfile p = new ApnProfile();
            p.Type = modem.ApnTypes.Contains("IPV4V6") ? "IPV4V6" : modem.ApnTypes[0];
            p.Auth = modem.ApnAuth ? 0 : -1;
            bool setAuth;
            string password;
            if (!Ask(p, FreeIds(), out setAuth, out password)) return;
            if (await Run("Adding APN profile " + p.Id + "...", delegate { return modem.SaveApnProfile(p, setAuth, password); },
                "The modem did not accept the new profile.")) { }
            await Reload();
        }

        private async void OnEdit()
        {
            ApnProfile old = Selected;
            if (working || old == null) return;
            ApnProfile p = new ApnProfile();
            p.Id = old.Id; p.Type = old.Type; p.Apn = old.Apn; p.Auth = old.Auth; p.User = old.User;
            bool setAuth;
            string password;
            if (!Ask(p, null, out setAuth, out password)) return;
            if (p.Apn == old.Apn && p.Type == old.Type && !setAuth) return;

            string question = "Change profile " + p.Id + "?\n\n"
                + "APN:  " + ApnText(old.Apn) + "  ->  " + ApnText(p.Apn) + "\n"
                + "IP type:  " + old.Type + "  ->  " + p.Type + "\n"
                + (setAuth ? "Sign-in:  " + ApnProfile.AuthName(p.Auth) + (p.Auth != 0 && p.User.Length > 0 ? ", user " + p.User : "")
                    + " (replaces the stored sign-in data)\n" : "")
                + "\nThe change is stored in the modem.";
            if (MessageBox.Show(this, question, "APN profiles", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;

            await Run("Changing APN profile " + p.Id + "...", delegate { return modem.SaveApnProfile(p, setAuth, password); },
                "The modem did not accept the change.");
            await Reload();
        }

        private async void OnDelete()
        {
            ApnProfile p = Selected;
            if (working || p == null) return;
            if (MessageBox.Show(this, "Delete profile " + p.Id + " (" + ApnText(p.Apn) + ") from the modem?",
                "APN profiles", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            await Run("Deleting APN profile " + p.Id + "...", delegate { return modem.DeleteApnProfile(p.Id); },
                "The modem did not delete the profile.");
            await Reload();
        }

        private static string ApnText(string apn)
        {
            return apn.Length > 0 ? apn : "(network default)";
        }

        /// <summary>
        /// Shows the profile editor. <paramref name="freeIds"/> is given for a new profile and null when
        /// editing an existing one. Returns false if the user cancelled.
        /// </summary>
        private bool Ask(ApnProfile p, List<int> freeIds, out bool setAuth, out string password)
        {
            setAuth = false;
            password = "";
            bool isNew = freeIds != null;
            int oldAuth = p.Auth;
            string oldUser = p.User;

            using (Form dlg = new Form())
            {
                dlg.Text = isNew ? "New APN profile" : "APN profile " + p.Id;
                dlg.Font = Font;
                dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
                dlg.MaximizeBox = dlg.MinimizeBox = false;
                dlg.ShowInTaskbar = false;
                dlg.StartPosition = FormStartPosition.CenterParent;
                dlg.AutoScaleDimensions = new SizeF(96f, 96f);
                dlg.AutoScaleMode = AutoScaleMode.Dpi;

                int y = 14;
                ComboBox idBox = null;
                if (isNew)
                {
                    idBox = new ComboBox();
                    idBox.DropDownStyle = ComboBoxStyle.DropDownList;
                    foreach (int id in freeIds) idBox.Items.Add(id);
                    idBox.SelectedIndex = 0;
                    AddRow(dlg, "Profile number:", idBox, ref y, 70);
                }

                TextBox apnBox = new TextBox();
                apnBox.Text = p.Apn;
                apnBox.MaxLength = 100;
                AddRow(dlg, "APN:", apnBox, ref y, 250);

                ComboBox typeBox = new ComboBox();
                typeBox.DropDownStyle = ComboBoxStyle.DropDownList;
                foreach (string t in modem.ApnTypes) typeBox.Items.Add(t);
                if (!modem.ApnTypes.Contains(p.Type)) typeBox.Items.Add(p.Type);
                typeBox.SelectedItem = p.Type;
                AddRow(dlg, "IP type:", typeBox, ref y, 100);

                ComboBox authBox = null;
                TextBox userBox = null, passBox = null;
                if (modem.ApnAuth)
                {
                    authBox = new ComboBox();
                    authBox.DropDownStyle = ComboBoxStyle.DropDownList;
                    for (int i = 0; i <= 3; i++) authBox.Items.Add(ApnProfile.AuthName(i));
                    authBox.SelectedIndex = p.Auth >= 0 && p.Auth <= 3 ? p.Auth : 0;
                    AddRow(dlg, "Sign-in:", authBox, ref y, 120);

                    userBox = new TextBox();
                    userBox.Text = p.User;
                    userBox.MaxLength = 64;
                    AddRow(dlg, "User name:", userBox, ref y, 180);

                    passBox = new TextBox();
                    passBox.UseSystemPasswordChar = true;
                    passBox.MaxLength = 64;
                    AddRow(dlg, "Password:", passBox, ref y, 180);

                    Label passNote = new Label();
                    passNote.ForeColor = Color.FromArgb(0x55, 0x55, 0x55);
                    passNote.Text = "The modem never shows the stored password. Leave the three sign-in fields as they are "
                        + "to keep it; changing any of them stores exactly what is entered here.";
                    passNote.SetBounds(14, y, 372, 46);
                    dlg.Controls.Add(passNote);
                    y += 50;

                    ComboBox ab = authBox; TextBox ub = userBox, pb = passBox;
                    EventHandler sync = delegate { ub.Enabled = pb.Enabled = ab.SelectedIndex > 0; };
                    authBox.SelectedIndexChanged += sync;
                    sync(null, EventArgs.Empty);
                }

                Button ok = new Button();
                ok.Text = "OK";
                ok.SetBounds(230, y + 6, 75, 26);
                Button cancel = new Button();
                cancel.Text = "Cancel";
                cancel.DialogResult = DialogResult.Cancel;
                cancel.SetBounds(311, y + 6, 75, 26);
                dlg.Controls.Add(ok);
                dlg.Controls.Add(cancel);
                dlg.AcceptButton = ok;
                dlg.CancelButton = cancel;
                dlg.ClientSize = new Size(400, y + 44);

                ok.Click += delegate
                {
                    string apn = apnBox.Text.Trim();
                    string problem = null;
                    if (!Regex.IsMatch(apn, @"^[A-Za-z0-9._-]*$"))
                        problem = "An APN consists of letters, digits, dots and hyphens only.";
                    else if (userBox != null && authBox.SelectedIndex > 0
                        && (Regex.IsMatch(userBox.Text, "[\"\\p{Cc}]") || Regex.IsMatch(passBox.Text, "[\"\\p{Cc}]")))
                        problem = "User name and password must not contain quotation marks.";
                    if (problem != null)
                    {
                        MessageBox.Show(dlg, problem, dlg.Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return;
                    }
                    dlg.DialogResult = DialogResult.OK;
                };

                if (dlg.ShowDialog(this) != DialogResult.OK) return false;

                if (isNew) p.Id = (int)idBox.SelectedItem;
                p.Apn = apnBox.Text.Trim();
                p.Type = (string)typeBox.SelectedItem;
                if (authBox != null)
                {
                    p.Auth = authBox.SelectedIndex;
                    p.User = p.Auth > 0 ? userBox.Text : "";
                    password = p.Auth > 0 ? passBox.Text : "";
                    // A new profile starts without sign-in data, so "None" needs no command there.
                    setAuth = isNew ? p.Auth > 0 : p.Auth != oldAuth || p.User != oldUser || password.Length > 0;
                }
                return true;
            }
        }

        private static void AddRow(Form dlg, string caption, Control field, ref int y, int width)
        {
            Label l = new Label();
            l.Text = caption;
            l.SetBounds(14, y + 3, 110, 20);
            field.SetBounds(128, y, width, 23);
            dlg.Controls.Add(l);
            dlg.Controls.Add(field);
            y += 30;
        }
    }
}
