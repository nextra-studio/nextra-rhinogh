// Reference supplied by the user (2026-10-11): Grasshopper C# script "Shiba Tidy Up".
// Adopted idea in Nextra: boundary relay params (_In / _Out) per group + micro/macro layout.
// Grasshopper Script Instance
#region Usings
using System;
using System.Linq;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

using Rhino;
using Rhino.Geometry;

using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using Grasshopper.Kernel.Special;
#endregion

public class Script_Instance : GH_ScriptInstance
{
    private static ShibaTidyWindow _window;

    private void RunScript(bool Launch)
    {
        if (Launch)
        {
            if (_window == null || _window.IsDisposed)
            {
                _window = new ShibaTidyWindow(GrasshopperDocument, this.Component);
                _window.Show(RhinoApp.MainWindow());
            }
            else
            {
                _window.BringToFront();
            }
        }
    }
}

// ==========================================================
// THE SHIBA TIDY-UP UI & LOGIC
// ==========================================================
public class ShibaTidyWindow : Form
{
    private CheckBox chkAnchor;
    private Button btnTidy;
    private Label lblStatus;

    private GH_Document ghDoc;
    private IGH_Component launcherComponent;

    public ShibaTidyWindow(GH_Document doc, IGH_Component component)
    {
        ghDoc = doc;
        launcherComponent = component;
        SetupModernUI();
    }

    private void SetupModernUI()
    {
        this.Text = "Shiba Tidy Up";
        this.Width = 420;
        this.Height = 220;
        this.FormBorderStyle = FormBorderStyle.FixedDialog;
        this.StartPosition = FormStartPosition.CenterScreen;
        this.TopMost = true;
        this.BackColor = Color.FromArgb(30, 30, 30);
        this.ForeColor = Color.White;
        this.Font = new Font("Segoe UI", 9.5f);

        Label lblHeader = new Label() { Text = "Script Organizer", Left = 20, Top = 15, Width = 200, Font = new Font("Segoe UI", 12, FontStyle.Bold), ForeColor = Color.FromArgb(200, 200, 200) };

        chkAnchor = new CheckBox() { Text = "Fallback Origin: Shiba Component (Unchecked: Original Position)", Left = 20, Top = 55, Width = 390, Checked = false, ForeColor = Color.LightSkyBlue };

        btnTidy = new Button() {
            Text = "Tidy Selected Components",
            Left = 20, Top = 90, Width = 365, Height = 45,
            BackColor = Color.FromArgb(0, 122, 204),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 10, FontStyle.Bold),
            Cursor = Cursors.Hand
        };
        btnTidy.FlatAppearance.BorderSize = 0;
        btnTidy.Click += (s, e) => RunTidyLogic();

        lblStatus = new Label() { Text = "Status: Select a cluster and click Tidy...", Left = 20, Top = 145, Width = 365, ForeColor = Color.FromArgb(150, 150, 150) };

        this.Controls.Add(lblHeader);
        this.Controls.Add(chkAnchor);
        this.Controls.Add(btnTidy);
        this.Controls.Add(lblStatus);
    }

    private void UpdateStatus(string message) {
        if (this.InvokeRequired) this.Invoke((Action)(() => lblStatus.Text = message));
        else lblStatus.Text = message;
    }

    private void RunTidyLogic()
    {
        bool useFallbackOrigin = chkAnchor.Checked;

        RhinoApp.InvokeOnUiThread((Action)(() => {
            try
            {
                var rawSelection = ghDoc.SelectedObjects().ToList();
                if (rawSelection.Count == 0) {
                    UpdateStatus("Please select components first!");
                    return;
                }

                var selectedObjs = new HashSet<IGH_DocumentObject>();
                var groups = new List<GH_Group>();

                foreach(var obj in rawSelection) {
                    if (obj == launcherComponent) continue;
                    if (obj is GH_Group grp) {
                        groups.Add(grp);
                        foreach (Guid id in grp.ObjectIDs) {
                            var gObj = ghDoc.FindObject(id, false);
                            if (gObj != null) selectedObjs.Add(gObj);
                        }
                    }
                    else selectedObjs.Add(obj);
                }
                if (selectedObjs.Count == 0) return;

                var objToGroup = new Dictionary<Guid, GH_Group>();
                foreach(var grp in groups) {
                    foreach(var memberGuid in grp.ObjectIDs) objToGroup[memberGuid] = grp;
                }

                var selGuids = new HashSet<Guid>(selectedObjs.Select(o => o.InstanceGuid));
                List<IGH_DocumentObject> allNewRelays = new List<IGH_DocumentObject>();

                var inRelayMap = new Dictionary<string, IGH_Param>();
                var outRelayMap = new Dictionary<string, IGH_Param>();
                HashSet<Guid> newRelayGuids = new HashSet<Guid>();

                UpdateStatus("Status: Analyzing and rewiring boundaries...");

                IGH_DocumentObject GetTopLevelObj(IGH_Param p) {
                    if (p == null) return null;
                    var top = p.Attributes?.GetTopLevel?.DocObject;
                    if (top != null) return top;
                    if (p is IGH_DocumentObject dObj) return dObj;
                    return null;
                }

                bool IsBoundaryWire(IGH_DocumentObject sourceObj, IGH_DocumentObject targetObj) {
                    if (sourceObj == null || targetObj == null) return false;
                    bool srcSel = selGuids.Contains(sourceObj.InstanceGuid);
                    bool tgtSel = selGuids.Contains(targetObj.InstanceGuid);
                    if (srcSel != tgtSel) return true;

                    if (srcSel && tgtSel) {
                        Guid srcGrp = objToGroup.ContainsKey(sourceObj.InstanceGuid) ? objToGroup[sourceObj.InstanceGuid].InstanceGuid : Guid.Empty;
                        Guid tgtGrp = objToGroup.ContainsKey(targetObj.InstanceGuid) ? objToGroup[targetObj.InstanceGuid].InstanceGuid : Guid.Empty;
                        return srcGrp != tgtGrp;
                    }
                    return false;
                }

                bool IsRelayParam(IGH_DocumentObject docObj) {
                    if (docObj == null) return false;
                    if (docObj is IGH_Component) return false;

                    if (docObj is GH_NumberSlider || docObj is GH_Panel || docObj is GH_BooleanToggle ||
                        docObj is GH_ValueList || docObj is GH_ColourSwatch || docObj is GH_GraphMapper)
                        return false;

                    return true;
                }

                // 1. PASS 1: INPUT BOUNDARIES
                foreach (var obj in selectedObjs.ToList()) {
                    GH_Group myGroup = objToGroup.ContainsKey(obj.InstanceGuid) ? objToGroup[obj.InstanceGuid] : null;
                    Guid myGroupGuid = myGroup != null ? myGroup.InstanceGuid : Guid.Empty;

                    if (obj is IGH_Component comp) {
                        foreach (var input in comp.Params.Input) {
                            var extSources = input.Sources.Where(s => {
                                var srcObj = GetTopLevelObj(s);
                                if (srcObj == null) return false;
                                if (IsRelayParam(srcObj) && !newRelayGuids.Contains(srcObj.InstanceGuid)) return false;
                                return IsBoundaryWire(srcObj, obj);
                            }).ToList();

                            foreach(var extSrc in extSources) {
                                string key = $"{myGroupGuid}_{extSrc.InstanceGuid}";
                                IGH_Param relay;
                                if (!inRelayMap.ContainsKey(key)) {
                                    relay = CreateRelayParam(input.TypeName, input.Name + "_In");
                                    if (relay == null) continue;

                                    ghDoc.AddObject(relay, false);
                                    newRelayGuids.Add(relay.InstanceGuid);
                                    inRelayMap[key] = relay;
                                    allNewRelays.Add(relay);
                                    if (myGroup != null) { myGroup.AddObject(relay.InstanceGuid); objToGroup[relay.InstanceGuid] = myGroup; }

                                    relay.AddSource(extSrc);
                                    relay.OnObjectChanged(GH_ObjectEventType.Sources);
                                } else { relay = inRelayMap[key]; }

                                input.RemoveSource(extSrc);
                                input.AddSource(relay);
                                input.OnObjectChanged(GH_ObjectEventType.Sources);
                            }
                        }
                    }
                }

                // 2. PASS 2: OUTPUT BOUNDARIES
                foreach (var obj in selectedObjs.ToList()) {
                    GH_Group myGroup = objToGroup.ContainsKey(obj.InstanceGuid) ? objToGroup[obj.InstanceGuid] : null;
                    Guid myGroupGuid = myGroup != null ? myGroup.InstanceGuid : Guid.Empty;

                    if (obj is IGH_Component comp || !IsRelayParam(obj)) {
                        var outputs = (obj is IGH_Component c) ? c.Params.Output : new List<IGH_Param> { (IGH_Param)obj };
                        foreach (var output in outputs) {
                            var extTargets = output.Recipients.Where(r => {
                                var tgtObj = GetTopLevelObj(r);
                                if (tgtObj == null) return false;
                                if (IsRelayParam(tgtObj) && !newRelayGuids.Contains(tgtObj.InstanceGuid)) return false;
                                return IsBoundaryWire(obj, tgtObj);
                            }).ToList();

                            if (extTargets.Count > 0) {
                                string key = $"{myGroupGuid}_{output.InstanceGuid}";
                                IGH_Param relay;
                                if (!outRelayMap.ContainsKey(key)) {
                                    string rName = (obj is IGH_Component) ? output.Name : obj.NickName;
                                    relay = CreateRelayParam(output.TypeName, rName + "_Out");
                                    if (relay == null) continue;
                                    ghDoc.AddObject(relay, false);
                                    newRelayGuids.Add(relay.InstanceGuid);
                                    outRelayMap[key] = relay;
                                    allNewRelays.Add(relay);
                                    if (myGroup != null) { myGroup.AddObject(relay.InstanceGuid); objToGroup[relay.InstanceGuid] = myGroup; }
                                    relay.AddSource(output);
                                    relay.OnObjectChanged(GH_ObjectEventType.Sources);
                                } else { relay = outRelayMap[key]; }

                                foreach(var tgt in extTargets) {
                                    tgt.RemoveSource(output);
                                    tgt.AddSource(relay);
                                    tgt.OnObjectChanged(GH_ObjectEventType.Sources);
                                }
                            }
                        }
                    }
                }
                // (3. two-layer micro/macro layout — same RunGripLogic as reference/nextra.gh/PluginWebSocketService.cs;
                //  _In params get rank max+1 (left column), _Out params rank min-1 (right column))
                ghDoc.NewSolution(false);
                UpdateStatus("Tidy Up Complete!");
            }
            catch (Exception ex)
            {
                UpdateStatus($"Logic Error: {ex.Message}");
            }
        }));
    }

    private IGH_Param CreateRelayParam(string typeName, string nickname) {
        var server = Instances.ComponentServer;
        if (typeName == "Generic Data") typeName = "Data";
        var proxy = server.ObjectProxies.FirstOrDefault(p => p.Desc.Name.Equals(typeName, StringComparison.OrdinalIgnoreCase) && p.Desc.Category == "Params");
        if (proxy == null) proxy = server.ObjectProxies.FirstOrDefault(p => p.Desc.Name.Equals(typeName + " Param", StringComparison.OrdinalIgnoreCase) && p.Desc.Category == "Params");
        if (proxy == null) proxy = server.ObjectProxies.FirstOrDefault(p => p.Desc.Name == "Data" && p.Desc.Category == "Params");
        if (proxy == null) return null;
        var param = (IGH_Param)proxy.CreateInstance();
        param.CreateAttributes();
        param.NickName = nickname;
        return param;
    }
}
