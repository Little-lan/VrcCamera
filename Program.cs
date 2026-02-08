using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Net;
using System.Threading.Tasks;
using System.Windows.Forms;
using Rug.Osc;

namespace VrcCameraDebugger
{
    // ==========================================
    // 1. 程序入口
    // ==========================================
    static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.SetHighDpiMode(HighDpiMode.SystemAware);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new DebugWindow());
        }
    }

    // ==========================================
    // 2. 自定义摇杆控件
    // ==========================================
    public class JoystickControl : Control
    {
        public float ValueX { get; private set; } = 0f;
        public float ValueY { get; private set; } = 0f;
        public bool IsUserInteracting { get; private set; } = false;

        private Point centerPos;
        private Point knobPos;
        private int maxRadius = 0;

        public JoystickControl()
        {
            SetStyle(ControlStyles.SupportsTransparentBackColor, true);
            this.BackColor = Color.Transparent;
            this.Size = new Size(150, 150);
            this.DoubleBuffered = true;
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            centerPos = new Point(Width / 2, Height / 2);
            knobPos = centerPos;
            maxRadius = (Math.Min(Width, Height) / 2) - 20;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            IsUserInteracting = true;
            UpdateKnob(e.Location);
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (IsUserInteracting) UpdateKnob(e.Location);
            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            IsUserInteracting = false;
            knobPos = centerPos;
            ValueX = 0f;
            ValueY = 0f;
            this.Invalidate();
            base.OnMouseUp(e);
        }

        private void UpdateKnob(Point mouseLoc)
        {
            int dx = mouseLoc.X - centerPos.X;
            int dy = mouseLoc.Y - centerPos.Y;
            double distance = Math.Sqrt(dx * dx + dy * dy);

            if (distance > maxRadius)
            {
                double ratio = maxRadius / distance;
                dx = (int)(dx * ratio);
                dy = (int)(dy * ratio);
            }

            knobPos = new Point(centerPos.X + dx, centerPos.Y + dy);
            ValueX = dx / (float)maxRadius;
            ValueY = dy / (float)maxRadius;
            this.Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var brush = new SolidBrush(Color.FromArgb(50, 50, 50)))
            {
                e.Graphics.FillEllipse(brush, centerPos.X - maxRadius, centerPos.Y - maxRadius, maxRadius * 2, maxRadius * 2);
            }
            using (var pen = new Pen(Color.Gray, 2))
            {
                e.Graphics.DrawEllipse(pen, centerPos.X - maxRadius, centerPos.Y - maxRadius, maxRadius * 2, maxRadius * 2);
            }
            using (var brush = new SolidBrush(Color.IndianRed))
            {
                e.Graphics.FillEllipse(brush, knobPos.X - 20, knobPos.Y - 20, 40, 40);
            }
        }
    }

    // ==========================================
    // 3. 主窗口逻辑
    // ==========================================
    public class DebugWindow : Form
    {
        private OscSender? sender;
        private OscReceiver? receiver;
        private bool isListening = true;
        private System.Windows.Forms.Timer loopTimer;

        // UI 基础控件
        private Label lblAddress, lblStatus, lblPosInfo, lblRotInfo;
        private TextBox txtSendAddress;
        private NumericUpDown numPosX, numPosY, numPosZ, numRotX, numRotY, numRotZ;
        private Button btnSend;
        private JoystickControl joyMove, joyLook;
        
        // 速度 & 模式
        private TrackBar trkSpeed;
        private Label lblSpeedValue;
        private CheckBox chkGimbalMode;

        // 模型参数同步
        private TextBox[] txtAuxAddrs;
        private CheckBox[] chkAuxEnables;

        // 高级功能控件
        private TrackBar? trkZoom, trkFocus, trkAperture, trkExposure, trkOpacity;
        private Label? lblZoomVal, lblFocusVal, lblApertureVal, lblExposureVal;
        
        private float[][] savedSlots = new float[3][]; 
        private Button[] btnSaves, btnLoads;

        private CheckBox chkLookAtMe;
        private CheckBox chkTopMost;
        private Button btnReset;

        public DebugWindow()
        {
            // --- 窗口基础设置 ---
            this.Text = "VRChat 无人机控制台"; // 固定标题
            this.Size = new Size(1450, 750); 
            this.StartPosition = FormStartPosition.CenterScreen;
            this.BackColor = Color.FromArgb(30, 30, 30);
            this.ForeColor = Color.White;
            this.DoubleBuffered = true;

            // 加载指定的 ICO 文件
            try { this.Icon = new Icon("b_f52429ee95e1e501f97afa6d1f9ebb30.ico"); } catch { }

            int margin = 20; 
            int col2 = 320; 
            int col3 = 600;
            int col4 = 850; 
            int col5 = 1150; 

            // ================= 列 1：接收监视 与 重置 =================
            CreateLabel("--- [接收 RX] 端口 9001 ---", margin, 20, 14, Color.LightGreen);
            lblAddress = CreateLabel("等待数据...", margin, 50, 10, Color.Gray);
            CreateLabel("当前坐标回显:", margin, 80, 12, Color.Cyan);
            lblPosInfo = CreateLabel("X/Y/Z: 0.0 / 0.0 / 0.0", margin, 110, 12);
            lblRotInfo = CreateLabel("P/Y/R: 0.0 / 0.0 / 0.0", margin, 140, 12);

            // 重置按钮
            btnReset = new Button() { 
                Text = "重置原点 (Reset All)", 
                Location = new Point(margin, 180), 
                Size = new Size(180, 35), 
                BackColor = Color.FromArgb(100, 40, 40), 
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            btnReset.FlatAppearance.BorderSize = 0;
            btnReset.Click += (s, e) => ResetPositions();
            this.Controls.Add(btnReset);

            // ================= 列 2：参数设置 =================
            CreateLabel("--- [参数设置] ---", col2, 20, 14, Color.LightSalmon);
            CreateLabel("目标地址:", col2, 50, 10);
            txtSendAddress = new TextBox() { Text = "/usercamera/Pose", Location = new Point(col2, 75), Size = new Size(200, 25) };
            this.Controls.Add(txtSendAddress);

            int inputY = 145; 
            int gapY = 40;

            CreateLabel("位置 (m):", col2, inputY - 25, 12, Color.Cyan);
            numPosX = CreateInput(col2 + 30, inputY, -10000, 10000);
            numPosY = CreateInput(col2 + 30, inputY + gapY, -10000, 10000);
            numPosZ = CreateInput(col2 + 30, inputY + gapY * 2, -10000, 10000);
            CreateLabel("X", col2 + 10, inputY + 5, 10); 
            CreateLabel("Y", col2 + 10, inputY + gapY + 5, 10); 
            CreateLabel("Z", col2 + 10, inputY + gapY * 2 + 5, 10);

            CreateLabel("旋转 (deg):", col2 + 140, inputY - 25, 12, Color.Orange);
            numRotX = CreateInput(col2 + 170, inputY, -360, 360);
            numRotY = CreateInput(col2 + 170, inputY + gapY, -360, 360);
            numRotZ = CreateInput(col2 + 170, inputY + gapY * 2, -360, 360);
            CreateLabel("P", col2 + 150, inputY + 5, 10); 
            CreateLabel("Y", col2 + 150, inputY + gapY + 5, 10); 
            CreateLabel("R", col2 + 150, inputY + gapY * 2 + 5, 10);

            btnSend = new Button() { Text = "直接设置当前位置", Location = new Point(col2, inputY + gapY * 3 + 10), Size = new Size(200, 40), BackColor = Color.FromArgb(60, 60, 60), ForeColor = Color.White };
            btnSend.Click += (s, e) => SendPoseData();
            this.Controls.Add(btnSend);

            lblStatus = CreateLabel("准备就绪", col2, inputY + gapY * 3 + 60, 10, Color.Gray);

            // ================= 列 3：摇杆控制 =================
            CreateLabel("--- [摇杆控制] ---", col3, 20, 14, Color.Yellow);
            CreateLabel("移动 (X / Z) [相对方向]", col3 + 35, 60, 10);
            joyMove = new JoystickControl() { Location = new Point(col3, 80) };
            this.Controls.Add(joyMove);

            chkGimbalMode = new CheckBox() { Text = "控制镜头俯仰 (Pitch)", Location = new Point(col3 + 35, 235), Size = new Size(200, 20), ForeColor = Color.LightSkyBlue };
            this.Controls.Add(chkGimbalMode);

            CreateLabel("升降/俯仰 & 旋转(Yaw)", col3 + 35, 260, 10);
            joyLook = new JoystickControl() { Location = new Point(col3, 280) };
            this.Controls.Add(joyLook);

            int sliderY = 450;
            CreateLabel("速度倍率:", col3, sliderY, 10, Color.LightSkyBlue);
            lblSpeedValue = CreateLabel("1.0x", col3 + 80, sliderY, 10, Color.LightSkyBlue);
            trkSpeed = new TrackBar() { Location = new Point(col3, sliderY + 25), Size = new Size(160, 45), Minimum = 1, Maximum = 50, Value = 10, TickFrequency = 5 };
            trkSpeed.Scroll += (s, e) => { lblSpeedValue.Text = $"{(trkSpeed.Value / 10.0f):F1}x"; };
            this.Controls.Add(trkSpeed);

            // ================= 列 4：模型参数同步 =================
            CreateLabel("--- [模型同步] ---", col4, 20, 14, Color.Violet);
            CreateLabel("同步相机数据给 Avatar", col4, 50, 10, Color.Gray);

            string[] labels = { "Pos X", "Pos Y", "Pos Z", "Rot P", "Rot Y", "Rot R" };
            string[] defaultParams = { 
                "/avatar/parameters/DroneX", "/avatar/parameters/DroneY", "/avatar/parameters/DroneZ", 
                "/avatar/parameters/DroneRotX", "/avatar/parameters/DroneRotY", "/avatar/parameters/DroneRotZ" 
            };

            txtAuxAddrs = new TextBox[6];
            chkAuxEnables = new CheckBox[6];

            int auxStartY = 80;
            int auxGap = 50;

            for (int i = 0; i < 6; i++)
            {
                int currentY = auxStartY + (i * auxGap);
                CreateLabel(labels[i] + ":", col4, currentY + 4, 10, Color.Cyan);
                txtAuxAddrs[i] = new TextBox() { Text = defaultParams[i], Location = new Point(col4 + 50, currentY), Size = new Size(160, 23) };
                this.Controls.Add(txtAuxAddrs[i]);
                chkAuxEnables[i] = new CheckBox() { Text = "启用", Location = new Point(col4 + 220, currentY + 2), Size = new Size(60, 20), ForeColor = Color.White };
                this.Controls.Add(chkAuxEnables[i]);
            }

            // ================= 列 5：高级功能 =================
            CreateLabel("--- [高级功能] ---", col5, 20, 14, Color.Gold);

            int lensY = 50;
            int lensGap = 85; 

            // Zoom
            CreateLabel("变焦 (Zoom/FOV):", col5, lensY, 10, Color.White);
            lblZoomVal = CreateLabel("45", col5 + 180, lensY, 10, Color.LightGray);
            trkZoom = CreateSlider(col5, lensY + 25, 20, 150, 45, (s, e) => {
                var tb = (TrackBar)s!;
                SendOscFloat("/usercamera/Zoom", tb.Value);
                if(lblZoomVal != null) lblZoomVal.Text = tb.Value.ToString();
            });

            // Aperture
            CreateLabel("光圈 (Aperture/F值):", col5, lensY + lensGap, 10, Color.White);
            lblApertureVal = CreateLabel("16.0", col5 + 180, lensY + lensGap, 10, Color.LightGray);
            trkAperture = CreateSlider(col5, lensY + lensGap + 25, 14, 320, 160, (s, e) => {
                var tb = (TrackBar)s!;
                float fStop = tb.Value / 10.0f; 
                SendOscFloat("/usercamera/Aperture", fStop);
                if(lblApertureVal != null) lblApertureVal.Text = fStop.ToString("F1");
            });

            // Focus
            CreateLabel("对焦距离 (Focus Dist):", col5, lensY + lensGap * 2, 10, Color.White);
            lblFocusVal = CreateLabel("1.5m", col5 + 180, lensY + lensGap * 2, 10, Color.LightGray);
            trkFocus = CreateSlider(col5, lensY + lensGap * 2 + 25, 1, 100, 15, (s, e) => {
                var tb = (TrackBar)s!;
                float dist = tb.Value / 10.0f; 
                SendOscFloat("/usercamera/FocalDistance", dist); 
                if(lblFocusVal != null) lblFocusVal.Text = dist.ToString("F1") + "m";
            });

            // Exposure
            CreateLabel("曝光 (Exposure):", col5, lensY + lensGap * 3, 10, Color.White);
            lblExposureVal = CreateLabel("0", col5 + 180, lensY + lensGap * 3, 10, Color.LightGray);
            trkExposure = CreateSlider(col5, lensY + lensGap * 3 + 25, -100, 40, 0, (s, e) => {
                var tb = (TrackBar)s!;
                float ev = tb.Value / 10.0f; 
                SendOscFloat("/usercamera/Exposure", ev); 
                if(lblExposureVal != null) lblExposureVal.Text = ev.ToString("F1");
            });

            // Presets
            int presetY = 380; 
            CreateLabel("位置存档 (Presets):", col5, presetY, 12, Color.Gold);
            
            btnSaves = new Button[3];
            btnLoads = new Button[3];
            for(int i=0; i<3; i++) {
                int py = presetY + 30 + (i * 40);
                int slotIndex = i; 
                btnSaves[i] = new Button() { Text = $"存 (Save {i+1})", Location = new Point(col5, py), Size = new Size(90, 30), BackColor = Color.DimGray, ForeColor = Color.White };
                btnSaves[i].Click += (s, e) => SavePreset(slotIndex);
                this.Controls.Add(btnSaves[i]);
                btnLoads[i] = new Button() { Text = $"读 (Load {i+1})", Location = new Point(col5 + 100, py), Size = new Size(90, 30), BackColor = Color.DimGray, ForeColor = Color.White };
                btnLoads[i].Click += (s, e) => LoadPreset(slotIndex);
                this.Controls.Add(btnLoads[i]);
            }

            // Tracking
            int miscY = 530;
            CreateLabel("智能追踪:", col5, miscY, 12, Color.Gold);
            chkLookAtMe = new CheckBox() { Text = "Look At Me (自动对脸)", Location = new Point(col5, miscY + 30), Size = new Size(200, 25), ForeColor = Color.White };
            chkLookAtMe.CheckedChanged += (s, e) => SendOscBool("/usercamera/LookAtMe", chkLookAtMe.Checked);
            this.Controls.Add(chkLookAtMe);

            // Window Settings
            int winY = 600;
            CreateLabel("窗口设置:", col5, winY, 12, Color.Gold);
            chkTopMost = new CheckBox() { Text = "窗口置顶 (Always Top)", Location = new Point(col5, winY + 30), Size = new Size(200, 25), ForeColor = Color.White };
            chkTopMost.CheckedChanged += (s, e) => { this.TopMost = chkTopMost.Checked; };
            this.Controls.Add(chkTopMost);
            CreateLabel("透明度:", col5, winY + 60, 10);
            trkOpacity = CreateSlider(col5, winY + 85, 20, 100, 100, (s, e) => {
                var tb = (TrackBar)s!;
                this.Opacity = tb.Value / 100.0;
            });
            trkOpacity.Width = 200;

            loopTimer = new System.Windows.Forms.Timer() { Interval = 15 };
            loopTimer.Tick += LoopTimer_Tick;
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            InitializeNetwork();
            EnableVRChatSmoothing();
            loopTimer.Start();
        }

        private void LoopTimer_Tick(object? sender, EventArgs e)
        {
            if (!joyMove.IsUserInteracting && !joyLook.IsUserInteracting) return;

            float speedMultiplier = trkSpeed.Value / 10.0f;
            float moveSpeed = 0.15f * speedMultiplier; 
            float rotSpeed = 1.5f * speedMultiplier;

            float inputStrafe = joyMove.ValueX * moveSpeed; 
            float inputForward = -joyMove.ValueY * moveSpeed; 
            
            float currentYaw = (float)numRotY.Value;
            double angleRad = currentYaw * Math.PI / 180.0;

            float globalDeltaX = (float)(inputStrafe * Math.Cos(angleRad) + inputForward * Math.Sin(angleRad));
            float globalDeltaZ = (float)(inputForward * Math.Cos(angleRad) - inputStrafe * Math.Sin(angleRad));

            float newY = (float)numPosY.Value;
            float newPitch = (float)numRotX.Value;

            if (chkGimbalMode.Checked)
                newPitch = (float)numRotX.Value + (joyLook.ValueY * rotSpeed); 
            else
                newY = (float)numPosY.Value - (joyLook.ValueY * moveSpeed);

            float newYaw = (float)numRotY.Value + (joyLook.ValueX * rotSpeed);
            float newX = (float)numPosX.Value + globalDeltaX;
            float newZ = (float)numPosZ.Value + globalDeltaZ;

            numPosX.Value = Clamp(newX, (float)numPosX.Minimum, (float)numPosX.Maximum);
            numPosZ.Value = Clamp(newZ, (float)numPosZ.Minimum, (float)numPosZ.Maximum);
            numPosY.Value = Clamp(newY, (float)numPosY.Minimum, (float)numPosY.Maximum);
            numRotX.Value = Clamp(newPitch, -90, 90);
            numRotY.Value = Clamp(newYaw, -360, 360);

            SendPoseData();
        }

        // --- 核心：重置所有功能 ---
        private void ResetPositions()
        {
            if (MessageBox.Show("确定要将所有参数（坐标、旋转、镜头）归零吗？\n相机将回到世界原点。", "确认重置", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
            {
                // 1. 重置坐标与旋转
                numPosX.Value = 0; numPosY.Value = 0; numPosZ.Value = 0;
                numRotX.Value = 0; numRotY.Value = 0; numRotZ.Value = 0;
                
                // 2. 发送坐标重置
                SendPoseData(); 

                // 3. 重置高级功能 (更新滑块UI + 发送OSC)
                if (trkZoom != null) { 
                    trkZoom.Value = 45; 
                    SendOscFloat("/usercamera/Zoom", 45);
                    if(lblZoomVal != null) lblZoomVal.Text = "45";
                }
                
                if (trkAperture != null) { 
                    trkAperture.Value = 160; // F16
                    SendOscFloat("/usercamera/Aperture", 16.0f);
                    if(lblApertureVal != null) lblApertureVal.Text = "16.0";
                }

                if (trkFocus != null) { 
                    trkFocus.Value = 15; // 1.5m
                    SendOscFloat("/usercamera/FocalDistance", 1.5f);
                    if(lblFocusVal != null) lblFocusVal.Text = "1.5m";
                }

                if (trkExposure != null) { 
                    trkExposure.Value = 0; // 0 EV
                    SendOscFloat("/usercamera/Exposure", 0);
                    if(lblExposureVal != null) lblExposureVal.Text = "0";
                }
            }
        }

        private void SavePreset(int slot)
        {
            savedSlots[slot] = new float[] {
                (float)numPosX.Value, (float)numPosY.Value, (float)numPosZ.Value,
                (float)numRotX.Value, (float)numRotY.Value, (float)numRotZ.Value
            };
            if(btnLoads != null && btnLoads[slot] != null) 
            {
                btnLoads[slot].BackColor = Color.LightGreen;
                btnLoads[slot].ForeColor = Color.Black;
            }
        }

        private void LoadPreset(int slot)
        {
            if (savedSlots[slot] == null) return;
            var data = savedSlots[slot];
            numPosX.Value = (decimal)data[0];
            numPosY.Value = (decimal)data[1];
            numPosZ.Value = (decimal)data[2];
            numRotX.Value = (decimal)data[3];
            numRotY.Value = (decimal)data[4];
            numRotZ.Value = (decimal)data[5];
            SendPoseData();
        }

        private decimal Clamp(float val, float min, float max) => (decimal)(val < min ? min : (val > max ? max : val));
        private void SendOscFloat(string addr, float val) { if (sender != null) try { sender.Send(new OscMessage(addr, val)); } catch {} }
        private void SendOscBool(string addr, bool val) { if (sender != null) try { sender.Send(new OscMessage(addr, val)); } catch {} }

        private void SendPoseData()
        {
            if (this.sender == null) return;
            try {
                this.sender.Send(new OscMessage(txtSendAddress.Text, (float)numPosX.Value, (float)numPosY.Value, (float)numPosZ.Value, (float)numRotX.Value, (float)numRotY.Value, (float)numRotZ.Value));
                
                float[] currentValues = { 
                    (float)numPosX.Value, (float)numPosY.Value, (float)numPosZ.Value, 
                    (float)numRotX.Value, (float)numRotY.Value, (float)numRotZ.Value 
                };

                for (int i = 0; i < 6; i++)
                {
                    if (chkAuxEnables[i].Checked)
                    {
                        string addr = txtAuxAddrs[i].Text.Trim();
                        if (!string.IsNullOrEmpty(addr)) this.sender.Send(new OscMessage(addr, currentValues[i]));
                    }
                }
                lblStatus.Text = "正在发送..."; lblStatus.ForeColor = Color.Lime;
            } catch { lblStatus.Text = "发送失败"; lblStatus.ForeColor = Color.Red; }
        }

        private void EnableVRChatSmoothing()
        {
            SendOscBool("/usercamera/SmoothMovement", true);
            SendOscFloat("/usercamera/SmoothingStrength", 5.0f);
        }

        private void InitializeNetwork() {
            Task.Run(() => {
                receiver = new OscReceiver(9001);
                try {
                    receiver.Connect();
                    while (isListening) {
                        var packet = receiver.Receive();
                        if (packet is OscMessage msg && this.IsHandleCreated && !this.Disposing) 
                            this.Invoke(new Action(() => UpdateRxUI(msg)));
                    }
                } catch { }
            });
            try { sender = new OscSender(IPAddress.Loopback, 0, 9000); sender.Connect(); } catch (Exception ex) { MessageBox.Show($"发送器错误: {ex.Message}"); }
        }

        private void UpdateRxUI(OscMessage msg) {
            if (msg.Address.Contains("Pose") && msg.Count >= 6) {
                lblPosInfo.Text = $"X: {msg[0]:F2}  Y: {msg[1]:F2}  Z: {msg[2]:F2}";
                lblRotInfo.Text = $"P: {msg[3]:F1}  Y: {msg[4]:F1}  R: {msg[5]:F1}";

                if (!joyMove.IsUserInteracting && !joyLook.IsUserInteracting)
                {
                    if (Math.Abs((float)numPosX.Value - (float)msg[0]) > 0.05f) numPosX.Value = Clamp((float)msg[0], -10000, 10000);
                    if (Math.Abs((float)numPosY.Value - (float)msg[1]) > 0.05f) numPosY.Value = Clamp((float)msg[1], -10000, 10000);
                    if (Math.Abs((float)numPosZ.Value - (float)msg[2]) > 0.05f) numPosZ.Value = Clamp((float)msg[2], -10000, 10000);
                }
            }
        }

        private Label CreateLabel(string text, int x, int y, int fontSize, Color? c = null) {
            Label l = new Label() { Text = text, Location = new Point(x, y), AutoSize = true, Font = new Font("Microsoft YaHei UI", fontSize, FontStyle.Regular), ForeColor = c ?? Color.White };
            this.Controls.Add(l); return l;
        }

        private NumericUpDown CreateInput(int x, int y, int min, int max) {
            NumericUpDown num = new NumericUpDown() { Location = new Point(x, y), Size = new Size(80, 25), DecimalPlaces = 2, Minimum = min, Maximum = max, Increment = 0.5M };
            this.Controls.Add(num); return num;
        }

        private TrackBar CreateSlider(int x, int y, int min, int max, int val, EventHandler scrollAction)
        {
            TrackBar tb = new TrackBar() { Location = new Point(x, y), Size = new Size(180, 45), Minimum = min, Maximum = max, Value = val, TickFrequency = (max-min)/10 };
            tb.Scroll += scrollAction;
            this.Controls.Add(tb);
            return tb;
        }

        protected override void OnFormClosing(FormClosingEventArgs e) {
            isListening = false; loopTimer?.Stop(); receiver?.Close(); sender?.Close(); base.OnFormClosing(e);
        }
    }
}