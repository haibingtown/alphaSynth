﻿/*
 * This file is part of alphaSynth.
 * Copyright (c) 2014, T3866, PerryCodes, Daniel Kuschny and Contributors, All rights reserved.
 * 
 * This library is free software; you can redistribute it and/or
 * modify it under the terms of the GNU Lesser General Public
 * License as published by the Free Software Foundation; either
 * version 3.0 of the License, or at your option any later version.
 * 
 * This library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU
 * Lesser General Public License for more details.
 * 
 * You should have received a copy of the GNU Lesser General Public
 * License along with this library.
 */
using System;
using AlphaSynth.Ds;
using AlphaSynth.Midi;
using AlphaSynth.Midi.Event;
using AlphaSynth.Synthesis;

namespace AlphaSynth
{
    /// <summary>
    /// This sequencer dispatches midi events to the synthesizer based on the current
    /// synthesize position. The sequencer does not consider the playback speed. 
    /// </summary>
    public class MidiFileSequencer
    {
        private readonly Synthesizer _synthesizer;

        private FastList<MidiFileSequencerTempoChange> _tempoChanges;
        private FastDictionary<int, SynthEvent> _firstProgramEventPerChannel;
        private FastList<SynthEvent> _synthData;
        private int _division;
        private int _eventIndex;

        /// <remarks>
        /// Note that this is not the actual playback position. It's the position where we are currently synthesizing at. 
        /// Depending on the buffer size of the output, this position is after the actual playback. 
        /// </remarks>
        private double _currentTime;


        private PlaybackRange _playbackRange;
        private double _playbackRangeStartTime;
        private double _playbackRangeEndTime;
        private double _endTime;

        public PlaybackRange PlaybackRange
        {
            get { return _playbackRange; }
            set
            {
                _playbackRange = value;
                if (value != null)
                {
                    _playbackRangeStartTime = TickPositionToTimePositionWithSpeed(value.StartTick, 1);
                    _playbackRangeEndTime = TickPositionToTimePositionWithSpeed(value.EndTick, 1);
                }
            }
        }

        public bool IsLooping { get; set; }

        /// <summary>
        /// Gets the duration of the song in ticks. 
        /// </summary>
        public int EndTick { get; private set; }

        /// <summary>
        /// Gets the duration of the song in milliseconds. 
        /// </summary>
        public double EndTime
        {
            get { return _endTime / PlaybackSpeed; }
        }

        /// <summary>
        /// Gets or sets the playback speed. 
        /// </summary>
        public double PlaybackSpeed
        {
            get; set;
        }

        public MidiFileSequencer(Synthesizer synthesizer)
        {
            _synthesizer = synthesizer;
            _firstProgramEventPerChannel = new FastDictionary<int, SynthEvent>();
            _tempoChanges = new FastList<MidiFileSequencerTempoChange>();
            PlaybackSpeed = 1;
        }

        public void Seek(double timePosition)
        {
            if (timePosition < 0)
            {
                timePosition = 0;
            }

            // map to speed=1
            timePosition *= PlaybackSpeed;

            // ensure playback range
            if (PlaybackRange != null)
            {
                if (timePosition < _playbackRangeStartTime)
                {
                    timePosition = _playbackRangeStartTime;
                }
                else if (timePosition > _playbackRangeEndTime)
                {
                    timePosition = _playbackRangeEndTime;
                }
            }

            if (timePosition > _currentTime)
            {
                SilentProcess(timePosition - _currentTime);
            }
            else if (timePosition < _currentTime)
            {
                //we have to restart the midi to make sure we get the right state: instruments, volume, pan, etc
                _currentTime = 0;
                _eventIndex = 0;
                _synthesizer.NoteOffAll(true);
                _synthesizer.ResetPrograms();
                _synthesizer.ResetSynthControls();

                SilentProcess(timePosition);
            }
        }

        private void SilentProcess(double milliseconds)
        {
            if (milliseconds <= 0) return;

            _currentTime += milliseconds;
            while (_eventIndex < _synthData.Count && _synthData[_eventIndex].Delta < (_currentTime))
            {
                var m = _synthData[_eventIndex];
                if (!m.IsMetronome)
                {
                    _synthesizer.ProcessMidiMessage(m.Event);   
                }
                _eventIndex++;
            }
        }


        public void LoadMidi(MidiFile midiFile)
        {
            _tempoChanges = new FastList<MidiFileSequencerTempoChange>();


            // Combine all tracks into 1 track that is organized from lowest to highest absolute time
            if (midiFile.Tracks.Length > 1 || midiFile.Tracks[0].EndTime == 0)
            {
                midiFile.CombineTracks();
            }
            _division = midiFile.Division;
            _eventIndex = 0;
            _currentTime = 0;

            // build synth events. 
            _synthData = new FastList<SynthEvent>();

            // Converts midi to milliseconds for easy sequencing
            double bpm = 120;
            var absTick = 0;
            var absTime = 0.0;

            var metronomeLength = 0;
            var metronomeTick = 0;
            var metronomeTime = 0.0;
            for (int x = 0; x < midiFile.Tracks[0].MidiEvents.Length; x++)
            {
                var mEvent = midiFile.Tracks[0].MidiEvents[x];

                var synthData = new SynthEvent(_synthData.Count, mEvent);
                _synthData.Add(synthData);
                absTick += mEvent.DeltaTime;
                absTime += mEvent.DeltaTime * (60000.0 / (bpm * midiFile.Division));
                synthData.Delta = absTime;

                if (mEvent.Command == MidiEventTypeEnum.Meta && mEvent.Data1 == (int)MetaEventTypeEnum.Tempo)
                {
                    var meta = (MetaNumberEvent)mEvent;
                    bpm = MidiHelper.MicroSecondsPerMinute / (double)meta.Value;
                    _tempoChanges.Add(new MidiFileSequencerTempoChange(bpm, absTick, (int)(absTime)));
                }
                else if (mEvent.Command == MidiEventTypeEnum.Meta && mEvent.Data1 == (int)MetaEventTypeEnum.TimeSignature)
                {
                    var meta = (MetaDataEvent)mEvent;
                    var timeSignatureDenominator = (int)Math.Pow(2, meta.Data[1]);
                    metronomeLength = (int)(_division * (4.0 / timeSignatureDenominator));
                }
                else if (mEvent.Command == MidiEventTypeEnum.ProgramChange)
                {
                    var channel = mEvent.Channel;
                    if (!_firstProgramEventPerChannel.ContainsKey(channel))
                    {
                        _firstProgramEventPerChannel[channel] = synthData;
                    }
                }

                if (metronomeLength > 0)
                {
                    while (metronomeTick < absTick)
                    {
                        var metronome = SynthEvent.NewMetronomeEvent(_synthData.Count, metronomeLength);
                        _synthData.Add(metronome);
                        metronome.Delta = metronomeTime;

                        metronomeTick += metronomeLength;
                        metronomeTime += metronomeLength * (60000.0 / (bpm * midiFile.Division));
                    }
                }
            }

            _synthData.Sort((a, b) =>
            {
                if (a.Delta > b.Delta)
                {
                    return 1;
                }
                else if (a.Delta < b.Delta)
                {
                    return -1;
                }
                return a.EventIndex - b.EventIndex;
            });
            _endTime = absTime;
            EndTick = absTick;
        }


        public void FillMidiEventQueue()
        {
            var millisecondsPerBuffer = (_synthesizer.MicroBufferSize / (double)_synthesizer.SampleRate) * 1000 * PlaybackSpeed;
            for (int i = 0; i < _synthesizer.MicroBufferCount; i++)
            {
                _currentTime += millisecondsPerBuffer;
                while (_eventIndex < _synthData.Count && _synthData[_eventIndex].Delta < _currentTime)
                {
                    _synthesizer.DispatchEvent(i, _synthData[_eventIndex]);
                    _eventIndex++;
                }
            }
        }

        public double TickPositionToTimePosition(int tickPosition)
        {
            return TickPositionToTimePositionWithSpeed(tickPosition, PlaybackSpeed);
        }

        public int TimePositionToTickPosition(double timePosition)
        {
            return TimePositionToTickPositionWithSpeed(timePosition, PlaybackSpeed);
        }

        private double TickPositionToTimePositionWithSpeed(int tickPosition, double playbackSpeed)
        {
            var timePosition = 0.0;
            var bpm = 120.0;
            var lastChange = 0;

            // find start and bpm of last tempo change before time
            for (int i = 0; i < _tempoChanges.Count; i++)
            {
                var c = _tempoChanges[i];
                if (tickPosition < c.Ticks)
                {
                    break;
                }
                timePosition = c.Time;
                bpm = c.Bpm;
                lastChange = c.Ticks;
            }

            // add the missing millis
            tickPosition -= lastChange;
            timePosition += (tickPosition * (60000.0 / (bpm * _division)));

            return timePosition / playbackSpeed;
        }

        private int TimePositionToTickPositionWithSpeed(double timePosition, double playbackSpeed)
        {
            timePosition *= playbackSpeed;

            var ticks = 0;
            var bpm = 120.0;
            var lastChange = 0;

            // find start and bpm of last tempo change before time
            for (int i = 0; i < _tempoChanges.Count; i++)
            {
                var c = _tempoChanges[i];
                if (timePosition < c.Time)
                {
                    break;
                }
                ticks = c.Ticks;
                bpm = c.Bpm;
                lastChange = c.Time;
            }

            // add the missing ticks
            timePosition -= lastChange;
            ticks += (int)(timePosition / (60000.0 / (bpm * _division)));
            // we add 1 for possible rounding errors.(floating point issuses)
            return ticks + 1;
        }

        public event Action Finished;
        protected virtual void OnFinished()
        {
            var finished = Finished;
            if (finished != null)
            {
                finished();
            }
        }


        public void CheckForStop()
        {
            if (PlaybackRange == null && _currentTime >= _endTime)
            {
                _currentTime = 0;
                _eventIndex = 0;
                _synthesizer.NoteOffAll(true);
                _synthesizer.ResetPrograms();
                _synthesizer.ResetSynthControls();
                OnFinished();
            }
            else if (PlaybackRange != null && _currentTime >= _playbackRangeEndTime)
            {
                _currentTime = PlaybackRange.StartTick;
                _eventIndex = 0;
                _synthesizer.NoteOffAll(true);
                _synthesizer.ResetPrograms();
                _synthesizer.ResetSynthControls();
                OnFinished();
            }
        }

        public void SetChannelProgram(int channel, byte program)
        {
            if (_firstProgramEventPerChannel.ContainsKey(channel))
            {
                _firstProgramEventPerChannel[channel].Event.Data1 = program;
            }
        }
    }

    public class MidiFileSequencerTempoChange
    {
        public double Bpm { get; set; }
        public int Ticks { get; set; }
        public int Time { get; set; }

        public MidiFileSequencerTempoChange(double bpm, int ticks, int time)
        {
            Bpm = bpm;
            Ticks = ticks;
            Time = time;
        }
    }
}
