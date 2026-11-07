using System;
using System.Drawing;
using System.Windows.Forms;

namespace WindBotRulings
{
    /// <summary>
    /// 2026-11-07: 색 토큰(다크/라이트) — ApplyTheme 이 컨트롤마다 Color.FromArgb 를 반복하던 것을 한 곳으로 모았다.
    /// ListView/TabControl 은 소유자 그리기에서 이 팔레트를 그대로 쓴다.
    /// </summary>
    internal sealed class Palette
    {
        public Color Bg;        // 창
        public Color Bg2;       // 입력/목록 배경
        public Color Card;      // 목록 행(짝수)
        public Color Border;
        public Color Fg;
        public Color Sub;
        public Color Accent;
        public Color Sel;
        public Color Btn;
        public Color BtnHi;

        public static readonly Palette Dark = new Palette
        {
            Bg = Color.FromArgb(32, 32, 32),
            Bg2 = Color.FromArgb(45, 45, 48),
            Card = Color.FromArgb(40, 40, 43),
            Border = Color.FromArgb(70, 70, 76),
            Fg = Color.FromArgb(232, 232, 236),
            Sub = Color.FromArgb(170, 174, 182),
            Accent = Color.FromArgb(86, 156, 214),
            Sel = Color.FromArgb(47, 79, 111),
            Btn = Color.FromArgb(60, 60, 65),
            BtnHi = Color.FromArgb(74, 74, 80)
        };

        public static readonly Palette Light = new Palette
        {
            Bg = SystemColors.Control,
            Bg2 = Color.White,
            Card = Color.FromArgb(248, 249, 251),
            Border = Color.FromArgb(206, 210, 216),
            Fg = SystemColors.ControlText,
            Sub = Color.FromArgb(96, 100, 108),
            Accent = Color.FromArgb(37, 99, 235),
            Sel = Color.FromArgb(204, 228, 255),
            Btn = SystemColors.Control,
            BtnHi = Color.FromArgb(229, 233, 240)
        };

        /// <summary>버튼을 팔레트에 맞춘다(테마 전환 때마다 호출).</summary>
        public void StyleButton(Button b, bool primary = false)
        {
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 1;
            b.FlatAppearance.BorderColor = primary ? Accent : Border;
            b.BackColor = primary ? Accent : Btn;
            b.ForeColor = primary ? Color.White : Fg;
            b.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(108, 178, 236) : BtnHi;
            b.FlatAppearance.MouseDownBackColor = primary ? Accent : Btn;
            b.Cursor = Cursors.Hand;
            b.UseVisualStyleBackColor = false;
        }
    }

    /// <summary>컨트롤 생성 보조(여백/글꼴 통일).</summary>
    internal static class Ui
    {
        public static readonly Font Base = new Font("Malgun Gothic", 9f);
        public static readonly Font Small = new Font("Malgun Gothic", 8.25f);
        public const int Gap = 6;

        public static Label Text(string text, Color color)
        {
            return new Label
            {
                Text = text,
                ForeColor = color,
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleLeft,
                BackColor = Color.Transparent,
                Font = Base
            };
        }

        public static TextBox Input(string text, Color back, Color fore)
        {
            return new TextBox
            {
                Text = text,
                BackColor = back,
                ForeColor = fore,
                BorderStyle = BorderStyle.FixedSingle,
                Font = Base
            };
        }
    }
}
