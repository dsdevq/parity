using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;

namespace System.Windows.Forms
{
    public class CancelEventArgs : System.ComponentModel.CancelEventArgs { }
    public delegate void KeyEventHandler(object sender, KeyEventArgs e);
    public class KeyEventArgs : EventArgs { public Keys KeyCode; }
    public enum Keys { None, Enter, Escape, F5 }
    public enum DialogResult { None, OK, Cancel }
    public enum AnchorStyles { None = 0, Top = 1, Bottom = 2, Left = 4, Right = 8 }
    public enum DockStyle { None, Top, Bottom, Left, Right, Fill }
    public enum FormStartPosition { Manual, CenterScreen, CenterParent }
    public enum MessageBoxButtons { OK, OKCancel, YesNo }

    public class ControlCollection : List<Control>
    {
        public void AddRange(Control[] controls) => base.AddRange(controls);
    }

    public class ControlBindingsCollection : List<Binding>
    {
        public Binding Add(string propertyName, object dataSource, string dataMember) { var b = new Binding(propertyName, dataSource, dataMember); Add(b); return b; }
        public Binding Add(string propertyName, object dataSource, string dataMember, bool formattingEnabled) => Add(propertyName, dataSource, dataMember);
    }

    public class Binding
    {
        public Binding(string propertyName, object dataSource, string dataMember) { }
        public Binding(string propertyName, object dataSource, string dataMember, bool formattingEnabled) { }
    }

    public class Control : Component
    {
        public ControlCollection Controls { get; } = new ControlCollection();
        public ControlBindingsCollection DataBindings { get; } = new ControlBindingsCollection();
        public string Name { get; set; }
        public string Text { get; set; }
        public bool Enabled { get; set; } = true;
        public bool Visible { get; set; } = true;
        public int TabIndex { get; set; }
        public Point Location { get; set; }
        public Size Size { get; set; }
        public AnchorStyles Anchor { get; set; }
        public DockStyle Dock { get; set; }
        public object Tag { get; set; }
        public Control Parent { get; set; }
        public event EventHandler Click;
        public event EventHandler TextChanged;
        public event EventHandler Enter;
        public event EventHandler Leave;
        public event EventHandler Paint;
        public event CancelEventHandler Validating;
        public event EventHandler Validated;
        public event KeyEventHandler KeyDown;
        public event EventHandler VisibleChanged;
        public event EventHandler EnabledChanged;
        public void SuspendLayout() { }
        public void ResumeLayout(bool performLayout) { }
        public void PerformLayout() { }
        public void Focus() { }
        public void Invalidate() { }
        public void BringToFront() { }
    }

    public class ScrollableControl : Control { public bool AutoScroll { get; set; } }
    public class ContainerControl : ScrollableControl { }
    public class UserControl : ContainerControl { }
    public class Form : ContainerControl
    {
        public FormStartPosition StartPosition { get; set; }
        public DialogResult DialogResult { get; set; }
        public event EventHandler Load;
        public event EventHandler Shown;
        public event EventHandler FormClosing;
        protected virtual void OnLoad(EventArgs e) { }
        protected virtual void OnShown(EventArgs e) { }
        public DialogResult ShowDialog() => DialogResult.OK;
        public void Close() { }
    }

    public class Panel : ScrollableControl { }
    public class Label : Control { }
    public class Button : Control { }
    public class TextBox : Control { public bool ReadOnly { get; set; } public void SelectAll() { } }
    public class CheckBox : Control { public bool Checked { get; set; } public event EventHandler CheckedChanged; }
    public class ComboBox : Control
    {
        public object DataSource { get; set; }
        public string DisplayMember { get; set; }
        public string ValueMember { get; set; }
        public object SelectedValue { get; set; }
        public object SelectedItem { get; set; }
        public event EventHandler SelectedIndexChanged;
    }
    public class DataGridView : Control { public object DataSource { get; set; } }
    public class ColumnHeader : Component { public string Text { get; set; } public int Width { get; set; } public ColumnHeader() { } public ColumnHeader(string key) { } }
    public class ColumnHeaderCollection : List<ColumnHeader> { public void AddRange(ColumnHeader[] items) => base.AddRange(items); }
    public class ListView : Control { public ColumnHeaderCollection Columns { get; } = new ColumnHeaderCollection(); public bool FullRowSelect { get; set; } public event EventHandler SelectedIndexChanged; }
    public class ImageCollection : List<object> { public void SetKeyName(int index, string name) { } public void Add(string key, object image) { } }
    public class ImageList : Component { public ImageList() { } public ImageList(IContainer c) { } public ImageCollection Images { get; } = new ImageCollection(); public object ImageStream { get; set; } public Color TransparentColor { get; set; } }

    public class BindingSource : Component
    {
        public BindingSource() { }
        public BindingSource(IContainer container) { }
        public object DataSource { get; set; }
        public object Current { get; set; }
        public void ResetBindings(bool metadataChanged) { }
    }

    public class ErrorProvider : Component
    {
        public ErrorProvider() { }
        public ErrorProvider(IContainer container) { }
        public Control ContainerControl { get; set; }
        public void SetError(Control control, string value) { }
    }

    public class Timer : Component
    {
        public Timer() { }
        public Timer(IContainer container) { }
        public int Interval { get; set; }
        public event EventHandler Tick;
        public void Start() { }
        public void Stop() { }
    }

    public static class MessageBox
    {
        public static DialogResult Show(string text) => DialogResult.OK;
        public static DialogResult Show(string text, string caption, MessageBoxButtons buttons) => DialogResult.OK;
    }

    public static class Application { public static void DoEvents() { } }
}
