using System;
using System.CodeDom.Compiler;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;

namespace DesktopTool.Views;

public class DonationWindow : Window, IComponentConnector
{
	internal Button TabWechat;

	internal Button TabAlipay;

	internal Border WechatPanel;

	internal Border AlipayPanel;

	internal TextBlock TipText;

	private bool _contentLoaded;

	public DonationWindow()
	{
		InitializeComponent();
		SwitchToWechat();
		base.KeyDown += delegate(object s, KeyEventArgs e)
		{
			if (e.Key == Key.Escape)
			{
				Close();
			}
		};
	}

	private void CloseButton_Click(object sender, RoutedEventArgs e)
	{
		Close();
	}

	private void TabWechat_Click(object sender, RoutedEventArgs e)
	{
		if (WechatPanel.Visibility != Visibility.Visible)
		{
			SwitchToWechat();
		}
	}

	private void TabAlipay_Click(object sender, RoutedEventArgs e)
	{
		if (AlipayPanel.Visibility != Visibility.Visible)
		{
			SwitchToAlipay();
		}
	}

	private void SwitchToWechat()
	{
		WechatPanel.Visibility = Visibility.Visible;
		AlipayPanel.Visibility = Visibility.Collapsed;
		TipText.Text = "使用微信扫码，任意金额均可~";
		TabWechat.Background = Brushes.Green;
		TabAlipay.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF505866"));
	}

	private void SwitchToAlipay()
	{
		WechatPanel.Visibility = Visibility.Collapsed;
		AlipayPanel.Visibility = Visibility.Visible;
		TipText.Text = "使用支付宝扫码，任意金额均可~";
		TabAlipay.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF1677FF"));
		TabWechat.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF505866"));
	}

	[DebuggerNonUserCode]
	[GeneratedCode("PresentationBuildTasks", "8.0.28.0")]
	public void InitializeComponent()
	{
		if (!_contentLoaded)
		{
			_contentLoaded = true;
			Uri resourceLocater = new Uri("/DesktopTool;V2.8.43.0;component/views/donationwindow.xaml", UriKind.Relative);
			Application.LoadComponent(this, resourceLocater);
		}
	}

	[DebuggerNonUserCode]
	[GeneratedCode("PresentationBuildTasks", "8.0.28.0")]
	[EditorBrowsable(EditorBrowsableState.Never)]
	void IComponentConnector.Connect(int connectionId, object target)
	{
		switch (connectionId)
		{
		case 1:
			((Button)target).Click += CloseButton_Click;
			break;
		case 2:
			TabWechat = (Button)target;
			TabWechat.Click += TabWechat_Click;
			break;
		case 3:
			TabAlipay = (Button)target;
			TabAlipay.Click += TabAlipay_Click;
			break;
		case 4:
			WechatPanel = (Border)target;
			break;
		case 5:
			AlipayPanel = (Border)target;
			break;
		case 6:
			TipText = (TextBlock)target;
			break;
		default:
			_contentLoaded = true;
			break;
		}
	}
}
