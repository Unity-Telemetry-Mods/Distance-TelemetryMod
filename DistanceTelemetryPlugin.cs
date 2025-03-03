﻿using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using Events;
using Events.Car;
using Events.Game;
using Events.GameMode;
using Events.Player;
using Events.RaceEnd;

using System;
using System.Net;

using TelemetryLib.Telemetry;
using TelemetryLib;
using UnityEngine;

namespace com.drowhunter.DistanceTelemetryMod
{
    [BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
    public class DistanceTelemetryPlugin : BaseUnityPlugin
    {
        static ManualLogSource Log;
        private ConfigEntry<int> Port;

        TelemetryExtractor telemetryExtractor;

        PlayerEvents playerEvents;
        DistanceTelemetryData data;
        
        bool carDestroyed = false;

        
        UdpTelemetry<DistanceTelemetryData> udp;
        
        private Vector3 previousLocalVelocity = Vector3.zero;

        LocalPlayerControlledCar _car;

        private LocalPlayerControlledCar car 
        {
            get
            {
                if(_car == null)
                {                    
                    _car = G.Sys?.PlayerManager_?.LocalPlayers_?[0]?.playerData_?.LocalCar_;  
                    if(_car != null)
                    {
                        SubscribeToEvents();
                    }
                }

                return _car;
            }
        }

        

        public static void Echo(string caller, string message)
        {
            Log?.LogInfo(string.Format("[{0}] {1}", caller, message));
        }

        public static ConfigEntry<bool> MaxSteeringMod { get; set; }

        private void Start()
        {
            telemetryExtractor = new TelemetryExtractor();
        }

        private void Awake()
        {
            Log = Logger;
            Port = Config.Bind("Telemetry", "UDP Port", 12345, "Port to Send Telemetry");
            Echo(nameof(Awake), string.Format("{0} {1} loaded.", MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION));
            ConfigDefinition configDefinition = new ConfigDefinition("General", "Max Steering Mod", "Set the steering angle to the maximum value");
            MaxSteeringMod = Config.Bind("General", "Max Steering Mod", false, "Set the steering angle to the maximum value");


            udp = new UdpTelemetry<DistanceTelemetryData>(new UdpTelemetryConfig
            {
                SendAddress = new IPEndPoint(IPAddress.Loopback, 12345)
            });

        }

        private void OnDestroy()
        {
            Echo(nameof(OnDestroy), "Disposing Udp");
            udp?.Dispose();
        }

        private void OnEnable()
        {
            Echo(nameof(OnEnable), "Subscribing...");
            StaticEvent<LocalCarHitFinish.Data>.Subscribe(new StaticEvent<LocalCarHitFinish.Data>.Delegate(RaceEnded));
            StaticEvent<Go.Data>.Subscribe(new StaticEvent<Go.Data>.Delegate(RaceStarted));
            StaticEvent<PauseToggled.Data>.Subscribe(new StaticEvent<PauseToggled.Data>.Delegate(Toggle_Paused));
        }

        private void RaceStarted(Go.Data e)
        {
            data.IsRacing = true;
            reset();
        }
        

        private void RaceEnded(LocalCarHitFinish.Data e)
        {
            data.IsRacing = false;
            reset();
        }

        private void OnDisable()
        {
            Echo(nameof(OnDisable), "UnSubscribing...");

            StaticEvent<LocalCarHitFinish.Data>.Unsubscribe(new StaticEvent<LocalCarHitFinish.Data>.Delegate(RaceEnded));
            StaticEvent<Go.Data>.Unsubscribe(new StaticEvent<Go.Data>.Delegate(RaceStarted));
            StaticEvent<PauseToggled.Data>.Unsubscribe(new StaticEvent<PauseToggled.Data>.Delegate(Toggle_Paused));
            UnSubscribeFromEvents();
        }


        float _yaw = 0f;

        private void reset()
        {
            _yaw = 0;
        }

        private void FixedUpdate()
        {
            if(car == null)
            {
                return;
            }

            data.IsCarEnabled = car.ExistsAndIsEnabled();

            var cRigidbody = car.GetComponent<Rigidbody>();

            telemetryExtractor.Update(cRigidbody);

            var basic = telemetryExtractor.ExtractTelemetry(EulerType.Coaster);

            data.Pitch = basic.EulerAngles.x; 
            data.Yaw = basic.EulerAngles.y;  
            data.Roll = basic.EulerAngles.z;
            
            data.cForce = basic.CentripetalForce;

            data.AngularVelocityX = basic.LocalAngularVelocity.x;
            data.AngularVelocityY = basic.LocalAngularVelocity.y;
            data.AngularVelocityZ = basic.LocalAngularVelocity.z;

            data.VelocityX = basic.LocalVelocity.x;
            data.VelocityY = basic.LocalVelocity.y;
            data.VelocityZ = basic.LocalVelocity.z;

            data.AccelX = basic.Accel.x;
            data.AccelY = basic.Accel.y;
            data.AccelZ = basic.Accel.z;


            var car_logic = car.carLogic_;

            data.KPH = car_logic.CarStats_.GetKilometersPerHour();

            data.Boost = car_logic.CarDirectives_.Boost_;
            data.Grip = car_logic.CarDirectives_.Grip_;
            data.WingsOpen = car_logic.Wings_.WingsOpen_;            

            
            data.AllWheelsOnGround = car_logic.CarStats_.AllWheelsContacting_;
            data.IsCarIsActive = car.isActiveAndEnabled;
            data.IsGrav = cRigidbody.useGravity;
            

            data.TireFL = CalcSuspension(car_logic.CarStats_.WheelFL_, 0.5f);
            data.TireFR = CalcSuspension(car_logic.CarStats_.WheelFR_, 0.5f);
            data.TireBL = CalcSuspension(car_logic.CarStats_.wheelBL_, grip: 0.2f);
            data.TireBR = CalcSuspension(car_logic.CarStats_.WheelBR_, grip: 0.2f);

            data.IsCarDestroyed = carDestroyed;

            data.OrientationX = cRigidbody.transform.forward.x;
            data.OrientationY = cRigidbody.transform.rotation.y;
            data.OrientationZ = cRigidbody.transform.rotation.z;
            data.OrientationW = cRigidbody.transform.rotation.w;

            udp.Send(data);           

            

            float CalcSuspension(NitronicCarWheel wheel, float? maxAngle = null, float? grip = null)
            {
                if (maxAngle != null && MaxSteeringMod.Value)
                {
                    wheel.fullSteerAngle_ = maxAngle.Value;
                }

                var pos = Math.Abs(wheel.hubTrans_.localPosition.y);
                var suspension = wheel.SuspensionDistance_;


                var frac = pos / suspension;

                return (float)Maths.EnsureMapRange(pos, 0, suspension, 1, -1);
            }

        }
        
        
        private void SubscribeToEvents()
        {
            Echo("SubscribeToEvents", "Subscribing to player events");
            playerEvents = car.PlayerDataLocal_.Events_;
            
            playerEvents.Subscribe(new InstancedEvent<Impact.Data>.Delegate(LocalVehicle_Collided));
            playerEvents.Subscribe(new InstancedEvent<Death.Data>.Delegate(LocalVehicle_Destroyed));
            playerEvents.Subscribe(new InstancedEvent<CarRespawn.Data>.Delegate(LocalVehicle_Respawn));
            playerEvents.Subscribe(new InstancedEvent<Explode.Data>.Delegate(LocalVehicle_Exploded));
        }

        private void Toggle_Paused(PauseToggled.Data e)
        {
            Log.LogDebug("Paused " + e.paused_);
            data.GamePaused = e.paused_;
        }

        private void UnSubscribeFromEvents()
        {
            Log.LogInfo("Unsubscribing from player events");
            playerEvents.Unsubscribe(new InstancedEvent<Impact.Data>.Delegate(LocalVehicle_Collided));
            playerEvents.Unsubscribe(new InstancedEvent<Death.Data>.Delegate(LocalVehicle_Destroyed));
            playerEvents.Unsubscribe(new InstancedEvent<CarRespawn.Data>.Delegate(LocalVehicle_Respawn));
            playerEvents.Unsubscribe(new InstancedEvent<Explode.Data>.Delegate(LocalVehicle_Exploded));
        }
        
        private void LocalVehicle_Collided(Impact.Data data)
        {
            Echo("LocalVehicle_Collided", "Collided");
            carDestroyed = true;
        }

        private void LocalVehicle_Destroyed(Death.Data data)
        {
            Echo("LocalVehicle_Destroyed", "Destroyed");
            reset();
            carDestroyed = true;
        }

        private void LocalVehicle_Respawn(CarRespawn.Data data)
        {
            Echo("LocalVehicle_Respawn", "Respawned");
            reset();
            carDestroyed = false;
        }

        private void LocalVehicle_Exploded(Explode.Data data)
        {
            Echo("LocalVehicle_Exploded", "Exploded");
            carDestroyed = true;
        }
       
    }
}