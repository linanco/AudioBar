using System;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace AudioBar;

internal sealed class DefaultDeviceClient : IMMNotificationClient
{
	private readonly Action onDeviceChanged;

	public DefaultDeviceClient(Action onDeviceChanged)
	{
		this.onDeviceChanged = onDeviceChanged;
	}

	public void OnDefaultDeviceChanged(DataFlow flow, Role role, string deviceId)
	{
		if (flow == DataFlow.Render)
		{
			onDeviceChanged();
		}
	}

	public void OnDeviceStateChanged(string deviceId, DeviceState newState)
	{
	}

	public void OnDeviceAdded(string pwstrDeviceId)
	{
	}

	public void OnDeviceRemoved(string pwstrDeviceId)
	{
	}

	public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key)
	{
	}
}
