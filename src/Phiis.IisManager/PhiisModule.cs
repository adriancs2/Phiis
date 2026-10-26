using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using Microsoft.Web.Management.Client;
using Microsoft.Web.Management.Server;

namespace Phiis.IisManager
{
    /// <summary>Client side: registers the "Phiis" tile in IIS Manager's Features View.</summary>
    public sealed class PhiisModule : Module
    {
        protected override void Initialize(IServiceProvider serviceProvider, ModuleInfo moduleInfo)
        {
            base.Initialize(serviceProvider, moduleInfo);

            var controlPanel = (IControlPanel)GetService(typeof(IControlPanel));
            var pageInfo = new ModulePageInfo(
                this,
                typeof(PhiisPage),
                "Phiis (PHP)",
                "Configure PHP for IIS: versions, php.ini, extensions and health checks.",
                DrawIcon(16),
                DrawIcon(32));

            controlPanel.RegisterPage(ControlPanelCategoryInfo.Iis, pageInfo);
            controlPanel.RegisterPage(ControlPanelCategoryInfo.ApplicationDevelopment, pageInfo);
        }

        /// <summary>Icon drawn in code: no binary assets, and no third-party logos.</summary>
        internal static Bitmap DrawIcon(int size)
        {
            var bitmap = new Bitmap(size, size);
            using (var g = Graphics.FromImage(bitmap))
            using (var path = new GraphicsPath())
            using (var brush = new SolidBrush(Color.FromArgb(0x3B, 0x5B, 0x9A)))
            using (var font = new Font("Segoe UI", size * 0.42f, FontStyle.Bold, GraphicsUnit.Pixel))
            using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

                float r = size / 4f;
                float s = size - 1;
                path.AddArc(0, 0, r, r, 180, 90);
                path.AddArc(s - r, 0, r, r, 270, 90);
                path.AddArc(s - r, s - r, r, r, 0, 90);
                path.AddArc(0, s - r, r, r, 90, 90);
                path.CloseFigure();
                g.FillPath(brush, path);
                g.DrawString("Ph", font, Brushes.White, new RectangleF(0, 0, size, size), format);
            }
            return bitmap;
        }

        private static Icon _windowIcon;

        /// <summary>The same icon for Phiis dialogs' title bars (created once; its handle lives as long as IIS Manager).</summary>
        internal static Icon WindowIcon
        {
            get
            {
                if (_windowIcon == null)
                {
                    using (var bitmap = DrawIcon(32))
                    {
                        _windowIcon = Icon.FromHandle(bitmap.GetHicon());
                    }
                }
                return _windowIcon;
            }
        }
    }
}
