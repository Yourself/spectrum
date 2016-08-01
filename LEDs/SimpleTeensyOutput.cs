﻿using System.IO.Ports;
using Spectrum.Base;
using System.Threading;
using System.Collections.Concurrent;
using System.Linq;

namespace Spectrum.LEDs {

  /**
   * SimpleTeensyOutput is an API that can handle a single Teensy. It has no
   * conception of how many LEDs the Teensy is addressing - it just communicates
   *  a given index and color to the Teensy. When enabled, an output thread is
   *  started. When disabled, this thread exits.
   */
  public class SimpleTeensyOutput : Output {

    private SerialPort port;
    private ConcurrentQueue<byte[]> buffer;
    private bool separateThread;

    public SimpleTeensyOutput(string portName, bool separateThread) {
      this.port = new SerialPort(portName);
      this.buffer = new ConcurrentQueue<byte[]>();
      this.separateThread = separateThread;
    }

    private bool enabled;
    private Thread outputThread;
    public bool Enabled {
      get {
        lock (this.port) {
          return this.enabled;
        }
      }
      set {
        lock (this.port) {
          if (this.enabled == value) {
            return;
          }
          if (value) {
            if (this.separateThread) {
              this.outputThread = new Thread(OutputThread);
              this.outputThread.Start();
            } else {
              this.InitializeTeensies();
            }
          } else {
            if (this.outputThread != null) {
              this.outputThread.Abort();
              this.outputThread.Join();
              this.outputThread = null;
            } else {
              this.TerminateTeensies();
            }
          }
          this.enabled = value;
        }
      }
    }

    private void InitializeTeensies() {
      this.port.Open();
      this.buffer.Enqueue(new byte[] { 1 }); // start mode1 on Teensy
    }

    private void TerminateTeensies() {
      byte[] exit_buffer = new byte[] { 0, 0 }; // exits mode1 on Teensy
      this.port.Write(exit_buffer, 0, 2);
      this.port.Close();
      this.buffer = new ConcurrentQueue<byte[]>();
    }

    public void Update() {
      lock (this.port) {
        int num_messages = this.buffer.Count;
        if (num_messages == 0) {
          return;
        }
        byte[][] messages = new byte[num_messages][];
        for (int i = 0; i < num_messages; i++) {
          bool result = this.buffer.TryDequeue(out messages[i]);
          if (!result) {
            throw new System.Exception("Someone else is dequeueing!");
          }
        }
        byte[] bytes = messages.SelectMany(a => a).ToArray();
        int num_bytes = messages.Sum(a => a.Length);
        this.port.Write(bytes, 0, num_bytes);
      }
    }

    private void OutputThread() {
      this.InitializeTeensies();
      try {
        while (true) {
          this.Update();
        }
      } catch (ThreadAbortException) {
        this.TerminateTeensies();
      }
    }

    public void Flush() {
      this.buffer.Enqueue(new byte[] { 1, 0 });
    }

    public void SetPixel(int pixelIndex, int color) {
      int message = pixelIndex + 2;
      this.buffer.Enqueue(new byte[] {
        (byte)message,
        (byte)(message >> 8),
        (byte)color,
        (byte)(color >> 8),
        (byte)(color >> 16),
      });
    }

  }

}