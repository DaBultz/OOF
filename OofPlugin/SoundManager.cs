using SoundFlow.Abstracts.Devices;
using SoundFlow.Backends.MiniAudio;
using SoundFlow.Components;
using SoundFlow.Providers;
using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using AudioFormat = SoundFlow.Structs.AudioFormat;
using PlaybackState = SoundFlow.Enums.PlaybackState;

namespace OofPlugin;

internal class SoundManager : IDisposable {
  private readonly Configuration Configuration;
  private readonly DeadPlayersList DeadPlayersList;

  // sound
  private string soundFile;

  private MiniAudioEngine engine;
  private AudioPlaybackDevice playbackDevice;
  private AudioFormat audioFormat;


  internal CancellationTokenSource CancelToken;

  public SoundManager(OofPlugin plugin) {
    Configuration = plugin.Configuration;
    DeadPlayersList = plugin.DeadPlayersList;

    engine = new MiniAudioEngine();
    engine.UpdateAudioDevicesInfo();

    // Get the system default playback device 
    var defaultDevice = engine.PlaybackDevices.FirstOrDefault(x => x.IsDefault);
    audioFormat = AudioFormat.DvdHq;
    playbackDevice = engine.InitializePlaybackDevice(defaultDevice, audioFormat);

    LoadFile();
    
    CancelToken = new CancellationTokenSource();
    Task.Run(() => OofAudioPolling(CancelToken.Token));
  }

  public void LoadFile() {
    if (string.IsNullOrEmpty(Configuration.DefaultSoundImportPath)) {
      soundFile = Path.Combine(Dalamud.PluginInterface.AssemblyLocation.Directory!.FullName, "oof.wav");
      return;
    }

    soundFile = Configuration.DefaultSoundImportPath;
  }

  public void Stop() {
    // When an audio plays this will cause a tiny lag spike, but as this is only used in the ConfigWindow, it
    // should be fine
    playbackDevice.Stop();
  }

  public void Play(CancellationToken token, float volume = 1f) {
    _ = Task.Run(() => {
      var dataProvider = new StreamDataProvider(engine, audioFormat, File.OpenRead(soundFile));
      var player = new SoundPlayer(engine, audioFormat, dataProvider);
      playbackDevice.MasterMixer.AddComponent(player);
      playbackDevice.Start();
      
      // this cleans up after the playback ends
      player.PlaybackEnded += (_, _) => {
        player.Stop();
        playbackDevice.MasterMixer.RemoveComponent(player);
        player.Dispose();
      };
      
      if (player.State == PlaybackState.Playing) {
        player.Stop();
      }
      
      player.Volume = volume;
      player.Play();
    }, token);
  }

  private async Task OofAudioPolling(CancellationToken token) {
    while (!token.IsCancellationRequested) {
      try {
        await Task.Delay(200, token);

        if (DeadPlayersList == null || DeadPlayersList.DeadPlayers.Count == 0)
          continue;

        // Run on framework thread AND await it so exceptions are observed
        await Dalamud.Framework.RunOnFrameworkThread(() => {
          var localPlayer = Dalamud.ObjectTable.LocalPlayer;
          if (localPlayer is null)
            return;

          foreach (var player in DeadPlayersList.DeadPlayers) {
            if (player.DidPlayOof)
              continue;

            float volume = 1f;

            if (Configuration.DistanceBasedOof && player.Distance != Vector3.Zero) {
              var dist = Vector3.Distance(localPlayer.Position, player.Distance);
              volume = VolumeFromDist(dist);
            }

            Play(token, volume);
            player.DidPlayOof = true;
            break;
          }
        });
      }
      catch (OperationCanceledException) {
        // normal shutdown
        break;
      }
      catch (Exception ex) {
        Dalamud.Log.Error(ex, "OOF: OofAudioPolling crashed");
        // keep loop alive instead of dying forever
      }
    }
  }

  public float VolumeFromDist(float dist, float distMax = 30f) {
    dist = Math.Min(dist, distMax);

    var falloff = Configuration.DistanceFalloff > 0
      ? 3f - Configuration.DistanceFalloff * 3f
      : 2.999f;

    var vol = 1f - ((dist / distMax) * (1f / falloff));
    return Math.Max(Configuration.DistanceMinVolume, vol);
  }

  public async Task TestDistanceAudio(CancellationToken token) {
    async Task PlayTest(float volume) {
      if (token.IsCancellationRequested)
        return;

      Play(token, volume);
      await Task.Delay(700, token);
    }

    await PlayTest(VolumeFromDist(0));
    await PlayTest(VolumeFromDist(10));
    await PlayTest(VolumeFromDist(20));
    await PlayTest(VolumeFromDist(30));
  }

  public void Dispose() {
    CancelToken.Cancel();
    CancelToken.Dispose();
    
    playbackDevice.Dispose();
    engine.Dispose();
  }
}