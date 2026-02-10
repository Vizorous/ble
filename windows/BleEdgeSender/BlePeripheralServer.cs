using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace BleEdgeSender;

internal sealed class BlePeripheralServer
{
    public static readonly Guid ServiceUuid = Guid.Parse("8f98b0c3-10c2-4f8f-8e8d-3f567c9d2f01");
    public static readonly Guid InputCharacteristicUuid = Guid.Parse("8f98b0c3-10c2-4f8f-8e8d-3f567c9d2f02");

    private GattServiceProvider? _serviceProvider;
    private GattLocalCharacteristic? _inputCharacteristic;
    private readonly SemaphoreSlim _notifyLock = new(1, 1);

    public event Action<bool>? SubscriberStateChanged;

    public bool HasSubscriber => _inputCharacteristic?.SubscribedClients?.Count > 0;

    public async Task StartAsync()
    {
        var serviceResult = await GattServiceProvider.CreateAsync(ServiceUuid);
        if (serviceResult.Error != BluetoothError.Success)
        {
            throw new InvalidOperationException($"Failed to create GATT service: {serviceResult.Error}");
        }

        _serviceProvider = serviceResult.ServiceProvider;

        var parameters = new GattLocalCharacteristicParameters
        {
            CharacteristicProperties = GattCharacteristicProperties.Notify,
            UserDescription = "BLE edge input stream",
        };

        var characteristicResult = await _serviceProvider.Service.CreateCharacteristicAsync(InputCharacteristicUuid, parameters);
        if (characteristicResult.Error != BluetoothError.Success)
        {
            throw new InvalidOperationException($"Failed to create input characteristic: {characteristicResult.Error}");
        }

        _inputCharacteristic = characteristicResult.Characteristic;
        _inputCharacteristic.SubscribedClientsChanged += OnSubscribedClientsChanged;

        _serviceProvider.StartAdvertising(new GattServiceProviderAdvertisingParameters
        {
            IsConnectable = true,
            IsDiscoverable = true,
        });
    }

    public Task StopAsync()
    {
        if (_inputCharacteristic != null)
        {
            _inputCharacteristic.SubscribedClientsChanged -= OnSubscribedClientsChanged;
        }

        _serviceProvider?.StopAdvertising();
        _inputCharacteristic = null;
        _serviceProvider = null;
        return Task.CompletedTask;
    }

    public async Task<bool> SendPacketAsync(byte[] payload, CancellationToken cancellationToken)
    {
        var characteristic = _inputCharacteristic;
        if (characteristic == null || !HasSubscriber)
        {
            return false;
        }

        await _notifyLock.WaitAsync(cancellationToken);
        try
        {
            using var writer = new DataWriter();
            writer.WriteBytes(payload);

            var status = await characteristic.NotifyValueAsync(writer.DetachBuffer());
            return status == GattCommunicationStatus.Success;
        }
        finally
        {
            _notifyLock.Release();
        }
    }

    private void OnSubscribedClientsChanged(GattLocalCharacteristic sender, object args)
    {
        SubscriberStateChanged?.Invoke(sender.SubscribedClients.Count > 0);
    }
}
