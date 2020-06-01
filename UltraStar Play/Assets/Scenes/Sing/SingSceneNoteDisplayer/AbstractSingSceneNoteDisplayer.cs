﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;
using UniInject;
using UniRx;

// Disable warning about fields that are never assigned, their values are injected.
#pragma warning disable CS0649

abstract public class AbstractSingSceneNoteDisplayer : MonoBehaviour, ISingSceneNoteDisplayer, INeedInjection, IExcludeFromSceneInjection, IInjectionFinishedListener
{
    public UiNote uiNotePrefab;
    public UiRecordedNote uiRecordedNotePrefab;

    public StarParticle perfectSentenceStarPrefab;

    public RectTransform uiNotesContainer;
    public RectTransform uiRecordedNotesContainer;
    public RectTransform uiEffectsContainer;

    public bool displayRoundedAndActualRecordedNotes;
    public bool showPitchOfNotes;

    [Inject]
    protected Settings settings;

    [Inject]
    protected SongMeta songMeta;

    [Inject]
    protected PlayerNoteRecorder playerNoteRecorder;

    [Inject(optional = true)]
    protected MicProfile micProfile;

    protected double beatsPerSecond;

    protected readonly List<UiRecordedNote> uiRecordedNotes = new List<UiRecordedNote>();
    protected readonly Dictionary<RecordedNote, List<UiRecordedNote>> recordedNoteToUiRecordedNotesMap = new Dictionary<RecordedNote, List<UiRecordedNote>>();
    protected readonly Dictionary<Note, UiNote> noteToUiNoteMap = new Dictionary<Note, UiNote>();

    protected Sentence displayedSentence;

    protected int avgMidiNote;

    // The number of rows on which notes can be placed.
    protected int noteRowCount;
    protected int maxNoteRowMidiNote;
    protected int minNoteRowMidiNote;
    protected float noteHeightPercent;

    abstract protected void PositionUiNote(RectTransform uiNote, int midiNote, double noteStartBeat, double noteEndBeat);

    abstract public void DisplaySentence(Sentence sentence, Sentence nextSentence);

    public virtual void OnInjectionFinished()
    {
        if (!enabled)
        {
            return;
        }

        beatsPerSecond = BpmUtils.GetBeatsPerSecond(songMeta);
        playerNoteRecorder.RecordedNoteStartedEventStream.Subscribe(recordedNoteStartedEvent =>
        {
            DisplayRecordedNote(recordedNoteStartedEvent.RecordedNote);
        });
        playerNoteRecorder.RecordedNoteContinuedEventStream.Subscribe(recordedNoteContinuedEvent =>
        {
            DisplayRecordedNote(recordedNoteContinuedEvent.RecordedNote);
        });
    }

    public void SetNoteRowCount(int noteRowCount)
    {
        // Notes can be placed on and between the drawn lines.
        this.noteRowCount = noteRowCount;
        // Check that there is at least one row for every possible note in an octave.
        if (this.noteRowCount < 12)
        {
            throw new UnityException(this.GetType() + " must be initialized with a row count >= 12 (one row for each note in an octave)");
        }

        noteHeightPercent = 1.0f / noteRowCount;
    }

    public void RemoveAllDisplayedNotes()
    {
        RemoveUiNotes();
        RemoveUiRecordedNotes();
    }

    public void DisplayRecordedNote(RecordedNote recordedNote)
    {
        if (recordedNote.TargetNote.Sentence != displayedSentence)
        {
            // This is probably a recorded note from the previous sentence that is still continued because of the mic delay.
            // Do not draw the recorded note, it is not in the displayed sentence.
            return;
        }

        // Freestyle notes are not drawn
        if (recordedNote.TargetNote.IsFreestyle)
        {
            return;
        }

        // Try to update existing recorded notes.
        if (recordedNoteToUiRecordedNotesMap.TryGetValue(recordedNote, out List<UiRecordedNote> uiRecordedNotes))
        {
            foreach (UiRecordedNote uiRecordedNote in uiRecordedNotes)
            {
                uiRecordedNote.TargetEndBeat = recordedNote.EndBeat;
            }
            return;
        }

        // Draw the bar for the rounded note
        // and draw the bar for the actually recorded pitch if needed.
        CreateUiRecordedNote(recordedNote, true);
        if (displayRoundedAndActualRecordedNotes && (recordedNote.RecordedMidiNote != recordedNote.RoundedMidiNote))
        {
            CreateUiRecordedNote(recordedNote, false);
        }
    }

    public void CreatePerfectNoteEffect(Note perfectNote)
    {
        if (noteToUiNoteMap.TryGetValue(perfectNote, out UiNote uiNote))
        {
            uiNote.CreatePerfectNoteEffect();
        }
    }

    protected void RemoveUiNotes()
    {
        foreach (Transform child in uiNotesContainer.transform)
        {
            Destroy(child.gameObject);
        }
        noteToUiNoteMap.Clear();
    }

    protected virtual UiNote CreateUiNote(Note note)
    {
        if (note.StartBeat == note.EndBeat)
        {
            return null;
        }

        UiNote uiNote = Instantiate(uiNotePrefab, uiNotesContainer);
        uiNote.Init(note, uiEffectsContainer);
        if (micProfile != null)
        {
            uiNote.SetColorOfMicProfile(micProfile);
        }

        Text uiNoteText = uiNote.lyricsUiText;
        string pitchName = MidiUtils.GetAbsoluteName(note.MidiNote);
        if (settings.GraphicSettings.showLyricsOnNotes && showPitchOfNotes)
        {
            uiNoteText.text = GetDisplayText(note) + " (" + pitchName + ")";
        }
        else if (settings.GraphicSettings.showLyricsOnNotes)
        {
            uiNoteText.text = GetDisplayText(note);
        }
        else if (showPitchOfNotes)
        {
            uiNoteText.text = pitchName;
        }
        else
        {
            uiNoteText.text = "";
        }

        RectTransform uiNoteRectTransform = uiNote.RectTransform;
        PositionUiNote(uiNoteRectTransform, note.MidiNote, note.StartBeat, note.EndBeat);

        noteToUiNoteMap[note] = uiNote;

        return uiNote;
    }

    public string GetDisplayText(Note note)
    {
        switch (note.Type)
        {
            case ENoteType.Freestyle:
                return $"<i><b><color=#c00000>{note.Text}</color></b></i>";
            case ENoteType.Golden:
                return $"<b>{note.Text}</b>";
            case ENoteType.Rap:
            case ENoteType.RapGolden:
                return $"<i><b><color=#ffa500ff>{note.Text}</color></b></i>";
            default:
                return note.Text;
        }
    }

    protected void RemoveUiRecordedNotes()
    {
        foreach (Transform child in uiRecordedNotesContainer.transform)
        {
            Destroy(child.gameObject);
        }
        uiRecordedNotes.Clear();
        recordedNoteToUiRecordedNotesMap.Clear();
    }

    protected void CreateUiRecordedNote(RecordedNote recordedNote, bool useRoundedMidiNote)
    {
        if (recordedNote.StartBeat == recordedNote.EndBeat)
        {
            return;
        }

        // Pitch detection algorithms often have issues finding the correct octave. However, the octave is irrelevant for scores.
        // When notes are drawn far away from the target note because the pitch detection got the wrong octave then it looks wrong.
        // Thus, only the relative pitch of the roundedMidiNote is used and drawn on the octave of the target note.
        int midiNote;
        if (useRoundedMidiNote)
        {
            midiNote = MidiUtils.GetMidiNoteOnOctaveOfTargetMidiNote(recordedNote.RoundedMidiNote, recordedNote.TargetNote.MidiNote);
        }
        else
        {
            midiNote = recordedNote.RecordedMidiNote;
        }

        UiRecordedNote uiNote = Instantiate(uiRecordedNotePrefab, uiRecordedNotesContainer);
        uiNote.RecordedNote = recordedNote;
        uiNote.StartBeat = recordedNote.StartBeat;
        uiNote.TargetEndBeat = recordedNote.EndBeat;
        // Draw already a portion of the note
        uiNote.LifeTimeInSeconds = Time.deltaTime;
        uiNote.EndBeat = recordedNote.StartBeat + (uiNote.LifeTimeInSeconds * beatsPerSecond);

        uiNote.MidiNote = midiNote;
        if (micProfile != null)
        {
            uiNote.SetColorOfMicProfile(micProfile);
        }

        Text uiNoteText = uiNote.lyricsUiText;
        if (showPitchOfNotes)
        {
            string pitchName = MidiUtils.GetAbsoluteName(midiNote);
            uiNoteText.text = " (" + pitchName + ")";
        }
        else
        {
            uiNoteText.text = "";
        }

        RectTransform uiNoteRectTransform = uiNote.RectTransform;
        PositionUiNote(uiNoteRectTransform, midiNote, uiNote.StartBeat, uiNote.EndBeat);

        uiRecordedNotes.Add(uiNote);
        recordedNoteToUiRecordedNotesMap.AddInsideList(recordedNote, uiNote);
    }

    public void CreatePerfectSentenceEffect()
    {
        for (int i = 0; i < 50; i++)
        {
            CreatePerfectSentenceStar();
        }
    }

    protected void CreatePerfectSentenceStar()
    {
        StarParticle star = Instantiate(perfectSentenceStarPrefab, uiEffectsContainer);
        RectTransform starRectTransform = star.GetComponent<RectTransform>();
        float anchorX = UnityEngine.Random.Range(0f, 1f);
        float anchorY = UnityEngine.Random.Range(0f, 1f);
        starRectTransform.anchorMin = new Vector2(anchorX, anchorY);
        starRectTransform.anchorMax = new Vector2(anchorX, anchorY);
        starRectTransform.anchoredPosition = Vector2.zero;

        star.RectTransform.localScale = Vector3.one * UnityEngine.Random.Range(0.2f, 0.6f);
        LeanTween.scale(star.RectTransform, Vector3.zero, 1f)
            .setOnComplete(() => Destroy(star.gameObject));
    }

    protected int CalculateNoteRow(int midiNote)
    {
        // Map midiNote to range of noteRows (wrap around).
        int wrappedMidiNote = midiNote;
        while (wrappedMidiNote > maxNoteRowMidiNote && wrappedMidiNote > 0)
        {
            // Reduce by one octave.
            wrappedMidiNote -= 12;
        }
        while (wrappedMidiNote < minNoteRowMidiNote && wrappedMidiNote < 127)
        {
            // Increase by one octave.
            wrappedMidiNote += 12;
        }
        // Calculate offset, such that the average note will be on the middle row
        // (thus, middle row has offset of zero).
        int offset = wrappedMidiNote - avgMidiNote;
        int noteRow = (noteRowCount / 2) + offset;
        return noteRow;
    }

    public Vector2 GetAnchorYForMidiNote(int midiNote)
    {
        int noteRow = CalculateNoteRow(midiNote);
        float anchorY = (float)noteRow / noteRowCount;
        float anchorYStart = anchorY - noteHeightPercent;
        float anchorYEnd = anchorY + noteHeightPercent;
        return new Vector2(anchorYStart, anchorYEnd);
    }

    protected void UpdateUiRecordedNoteEndBeat(UiRecordedNote uiRecordedNote)
    {
        uiRecordedNote.EndBeat = uiRecordedNote.StartBeat + (uiRecordedNote.LifeTimeInSeconds * beatsPerSecond);
        if (uiRecordedNote.EndBeat > uiRecordedNote.TargetEndBeat)
        {
            uiRecordedNote.EndBeat = uiRecordedNote.TargetEndBeat;
        }
    }
}