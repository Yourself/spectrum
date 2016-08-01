﻿using System;
using System.Collections.Generic;
using System.Linq;
using Spectrum.LEDs;
using Spectrum.Base;
using Spectrum.Hues;
using Spectrum.Audio;
using System.Threading;

namespace Spectrum {

  public class Visualizer {

    private Configuration config;
    private AudioInput audio;
    private HueOutput hue;
    private CartesianTeensyOutput teensy;

    private Random random;

    // FFT analysis dicts
    private Dictionary<String, double[]> bins;
    private Dictionary<String, float[]> energyHistory;
    private Dictionary<String, float> energyLevels;
    private int historyLength = 16;
    private int processCount;

    // analysis/history variables
    private bool silence = true;
    private int silentCounter = 0;
    public bool silentMode = true;
    private bool silentModeAlternatingFlag = false;
    private int silentModeHueIndex = 0;
    private int silentModeSatIndex = 254;
    private bool silentModeSatFall = false;
    private int silentModeLightIndex = 0;
    private bool kickPending = false;
    private bool snarePending = false;
    private bool totalMaxPossible = false;
    private bool totalMax = false;
    private int idleCounter = 0;
    private bool lightPending = false;
    private bool drop = false;
    private bool dropPossible = false;
    private int dropDuration = 0;
    private int target = 0;
    public int needupdate = 0;
    private float vol = 0;

    public Visualizer(
      Configuration config,
      AudioInput audio,
      HueOutput hue,
      CartesianTeensyOutput teensy
    ) {
      this.config = config;
      this.audio = audio;
      this.hue = hue;
      this.teensy = teensy;
      
      this.random = new Random();
      bins = new Dictionary<String, double[]>();
      energyHistory = new Dictionary<String, float[]>();
      energyLevels = new Dictionary<String, float>();

      // frequency detection bands
      // format: { bottom freq, top freq, activation level (delta)}
      bins.Add("midrange", new double[] { 250, 2000, .025 });
      bins.Add("total", new double[] { 60, 2000, .05 });
      // specific instruments
      bins.Add("kick", new double[] { 40, 50, .001 });
      bins.Add("snareattack", new double[] { 1500, 2500, .001 });
      foreach (String band in bins.Keys) {
        energyLevels.Add(band, 0);
        energyHistory.Add(band, Enumerable.Repeat((float)0, historyLength).ToArray());
      }
    }

    private bool enabled;
    private Thread inputThread;
    private Thread outputThread;
    public bool Enabled {
      get {
        lock (this.random) {
          return this.enabled;
        }
      }
      set {
        lock (this.random) {
          if (this.enabled == value) {
            return;
          }
          if (value) {
            this.inputThread = new Thread(AudioProcessingThread);
            this.inputThread.Start();
            this.outputThread = new Thread(LightProcessingThread);
            this.outputThread.Start();
          } else {
            this.inputThread.Abort();
            this.inputThread.Join();
            this.inputThread = null;
            this.outputThread.Abort();
            this.outputThread.Join();
            this.outputThread = null;
          }
          this.enabled = value;
        }
      }
    }

    private void AudioProcessingThread() {
      while (true) {
        if (!this.config.audioInputInSeparateThread) {
          this.audio.Update();
        }
        if (
          this.config.controlLights &&
          !this.config.lightsOff &&
          !this.config.redAlert
        ) {
          this.process(this.audio.AudioData, this.audio.Volume);
        }
      }
    }

    private void LightProcessingThread() {
      while (true) {
        this.updateHues();
        // Hue API limits 10/s light changes
        Thread.Sleep(100);
      }
    }

    // music pattern detection
    private void process(float[] spectrum, float level) {
      vol = level;
      processCount++;
      processCount = processCount % historyLength;
      for (int i = 1; i < spectrum.Length / 2; i++) {
        foreach (KeyValuePair<String, double[]> band in bins) {
          String name = band.Key;
          double[] window = band.Value;
          if (windowContains(window, i)) {
            energyLevels[name] += (spectrum[i] * spectrum[i]);
          }
        }
      }
      foreach (String band in energyHistory.Keys.ToList()) {
        float current = energyLevels[band];
        float[] history = energyHistory[band];
        float previous = history[(processCount + historyLength - 1) % historyLength];
        float change = current - previous;
        float avg = history.Average();
        float ssd = history.Select(val => (val - avg) * (val - avg)).Sum();
        float sd = (float)Math.Sqrt(ssd / historyLength);
        float threshold = (float)bins[band][2];
        bool signal = change > threshold;
        if (band == "total") {
          if (totalMaxPossible && change < 0) {
            totalMax = true;
            totalMaxPossible = false;
            if (dropPossible) {
              drop = true;
              dropPossible = false;
            }
          }
          if (current >= history.Max() && current > avg + this.config.peakC * sd) {
            // was: avg < .08
            if (current > 3 * avg && avg < this.config.dropQ && change > this.config.dropT && current > .26) {
              System.Diagnostics.Debug.WriteLine(probe(band, current, avg, sd, change));
              dropPossible = true;
            }
            totalMaxPossible = true;
          } else {
            dropPossible = false;
            totalMaxPossible = false;
          }
        }
        if (band == "kick") {
          if (current < avg || change < 0) {
          }
          // was: avg < .1, current > avg + 2 * sd
          if (current > avg + this.config.kickT * sd && avg < this.config.kickQ && current > .001) // !kickcounted here
          {
            if (totalMax) {
              System.Diagnostics.Debug.WriteLine(probe(band, current, avg, sd, change));
            }
            kickPending = true;
          }
        }
        if (band == "snareattack") {
          if (current > avg + this.config.snareT * sd && avg < this.config.snareQ && current > .001) // !snarecounted here
          {
            if (totalMax && current > .001) {
              System.Diagnostics.Debug.WriteLine(probe(band, current, avg, sd, change));
            }
            snarePending = true;
          }
        }
      }
      foreach (String band in energyHistory.Keys.ToList()) {
        energyHistory[band][processCount] = energyLevels[band];
        energyLevels[band] = 0;
      }
      silence = (level < .01) && silence;
    }

    // status update for hues
    private void updateHues() {
      if (!lightPending) {
        kickPending = kickPending && totalMax;
        snarePending = snarePending && totalMax;
        target = this.random.Next(5);
      }
      if (silentMode || !this.config.controlLights || this.config.lightsOff || this.config.redAlert) {
        if (silentMode || needupdate > 0) // not idling & nonzero lights need to be updated.
        {
          silentModeAlternatingFlag = !silentModeAlternatingFlag;
          if (!silentMode || silentModeAlternatingFlag) {
            silentModeHueIndex = (silentModeHueIndex + 10000) % 65535;
            silentModeLightIndex = (silentModeLightIndex + 1) % 5;
            this.hue.SendLightCommand(silentModeLightIndex, silent(silentModeHueIndex));
            needupdate--;
            System.Diagnostics.Debug.WriteLine("Updating.." + needupdate);
            System.Diagnostics.Debug.WriteLine("silentMode.." + silentMode);
          }
        }
      } else if (drop) {
        if (dropDuration == 0) {
          System.Diagnostics.Debug.WriteLine("dropOn");
          this.hue.SendGroupCommand(
            0,
            new HueCommand() {
              /*on = true,
              bri = 254,
              hue = this.random.Next(1, 65535),
              sat = 254,
              transitiontime = 0,
              effect = "colorloop",*/
              alert = "select",
            }
          );
        }
        // was: dropDuration == 1
        else if (dropDuration == 4) {
          /*this.hue.SendGroupCommand(
            0,
            new HueCommand() {
              bri = 1,
              effect = "colorloop",
              transitiontime = 2,
            }
          );*/
        } else if (dropDuration > 8) {
          System.Diagnostics.Debug.WriteLine("dropOff");
          drop = false;
          dropDuration = -1;
        }
        dropDuration++;
      } else if (kickPending) {
        if (lightPending) {
          this.hue.SendLightCommand(
            target,
            new HueCommand() {
              on = true,
              bri = 1,
              hue = 300,
              sat = 254,
              transitiontime = 2,
              alert = "none",
            }
          );
          lightPending = false;
          kickPending = false;
        } else {
          lightPending = true;
          System.Diagnostics.Debug.WriteLine("kickOn");
          this.hue.SendLightCommand(
            target,
            new HueCommand() {
              on = true,
              bri = 254,
              hue = 300,
              sat = 254,
              transitiontime = 1,
              alert = "none",
            }
          );
        }
      } else if (snarePending) // second highest priority: snare hit (?)
        {
        if (lightPending) {
          this.hue.SendLightCommand(
            target,
            new HueCommand() {
              on = true,
              bri = 1,
              hue = 43000,
              sat = 254,
              transitiontime = 2,
              alert = "none",
            }
          );
          snarePending = false;
          lightPending = false;
        } else {
          lightPending = true;
          System.Diagnostics.Debug.WriteLine("snareOn");
          this.hue.SendLightCommand(
            target,
            new HueCommand() {
              on = true,
              bri = 254,
              hue = 43000,
              sat = 254,
              transitiontime = 1,
              alert = "none",
            }
          );
        }
      } else {
        idleCounter++;
        // was: idlecounter > 4
        if (idleCounter > 2) {
          this.hue.SendLightCommand(
            target,
            new HueCommand() {
              on = false,
              bri = 0,
              sat = 254,
              transitiontime = 20,
              alert = "none",
              effect = "colorloop",
            }
          );
          idleCounter = 0;
        }
      }
      postUpdate();
      
      int numColumnsToLight = (int)(vol * 30);
      for (int j = 0; j < 40; j++) {
        for (int i = 0; i < 30; i++) {
          int color = numColumnsToLight > i ? 0x111111 : 0x000000;
          this.teensy.SetPixel(i, j, color);
        }
      }
      this.teensy.Flush();
      
      // run every tick of the timer
      if (silence) {
        System.Diagnostics.Debug.WriteLine("silence");
      }
      System.Diagnostics.Debug.WriteLine(vol);

      if (!this.config.hueOutputInSeparateThread) {
        this.hue.Update();
      }
      if (!this.config.ledsOutputInSeparateThread) {
        this.teensy.Update();
      }
    }

    private void postUpdate() {
      if (this.config.controlLights && silence && silentCounter > 40 && !silentMode) {
        System.Diagnostics.Debug.WriteLine("Silence detected.");
        silentMode = true;
      } else if (silence) {
        silentCounter++;
      }
      if (!silence) {
        silentCounter = 0;
        silentMode = false;
      }
      if (silentModeAlternatingFlag) {
        if (silentModeSatIndex < 127) {
          silentModeSatFall = false;
        }
        if (silentModeSatIndex > 380) {
          silentModeSatFall = true;
        }
        if (silentModeSatFall) {
          silentModeSatIndex--;
        } else {
          silentModeSatIndex++;
        }
      }
      // this will be changed in process() UNLESS level < .1 for the duration of process()
      silence = true;
      totalMax = false;
    }
    // math helper functions
    private bool windowContains(double[] window, int index) {
      return (freqToFFTBin(window[0]) <= index && freqToFFTBin(window[1]) >= index);
    }
    private int freqToFFTBin(double freq) {
      return (int)(freq / 2.69);
    }
    private int binWidth(String bin) {
      double[] window = bins[bin];
      return freqToFFTBin(window[1]) - freqToFFTBin(window[0]);
    }

    private String probe(String band, float current, float avg, float sd, float change) {
      return "Band:" + band + " cur:" + Math.Round(current * 10000) / 10000 + " avg:" + Math.Round(avg * 10000) / 10000 + " sd:" + Math.Round(sd * 10000) / 10000 + " delta:" + Math.Round(change * 10000) / 10000;
    }

    private HueCommand silent(int index) {
      if (this.config.lightsOff) {
        return new HueCommand() {
          on = false,
        };
      }
      if (this.config.redAlert) {
        return new HueCommand() {
          on = true,
          bri = 1,
          hue = 1,
          sat = 254,
          effect = "none",
        };
      } else if (this.config.controlLights) {
        return new HueCommand() {
          on = true,
          bri = 1,
          hue = index + 1,
          sat = Math.Min(silentModeSatIndex, 254),
          transitiontime = 12,
          effect = "none",
        };
      } else {
        int newbri = Math.Min(Math.Max(254 + 64 * this.config.brighten, 1), 254);
        int newsat = Math.Min(Math.Max(126 + 63 * this.config.sat, 0), 254);
        int newhue = Math.Min(Math.Max(16384 + this.config.colorslide * 4096, 0), 65535);
        return new HueCommand() {
          on = true,
          bri = newbri,
          hue = newhue,
          sat = newsat,
          effect = "none",
        };
      }
    }
  }
}