﻿using System.Collections.Generic;
using System.Linq;
using UniInject;
using UnityEngine;
using UnityEngine.UI;
using UniRx;
using System;

// Disable warning about fields that are never assigned, their values are injected.
#pragma warning disable CS0649

public class SentenceDisplayer : MonoBehaviour, INeedInjection, IExcludeFromSceneInjection, IInjectionFinishedListener
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
    private Settings settings;

    [Inject]
    private SongMeta songMeta;

    [Inject]
    private PlayerNoteRecorder playerNoteRecorder;

    [Inject(optional = true)]
    private MicProfile micProfile;

    private double beatsPerSecond;

    private readonly List<UiRecordedNote> uiRecordedNotes = new List<UiRecordedNote>();
    private readonly Dictionary<RecordedNote, List<UiRecordedNote>> recordedNoteToUiRecordedNotesMap = new Dictionary<RecordedNote, List<UiRecordedNote>>();
    private readonly Dictionary<Note, UiNote> noteToUiNoteMap = new Dictionary<Note, UiNote>();

    private Sentence displayedSentence;

    private int avgMidiNote;

    // The number of rows on which notes can be placed.
    private int noteRowCount;
    private int maxNoteRowMidiNote;
    private int minNoteRowMidiNote;
    private float noteHeightPercent;

    void Update()
    {
        // Draw the UiRecordedNotes smoothly from their StartBeat to TargetEndBeat
        foreach (UiRecordedNote uiRecordedNote in uiRecordedNotes)
        {
            if (uiRecordedNote.EndBeat < uiRecordedNote.TargetEndBeat)
            {
                uiRecordedNote.EndBeat = uiRecordedNote.StartBeat + (uiRecordedNote.LifeTimeInSeconds * beatsPerSecond);
                if (uiRecordedNote.EndBeat > uiRecordedNote.TargetEndBeat)
                {
                    uiRecordedNote.EndBeat = uiRecordedNote.TargetEndBeat;
                }

                PositionUiNote(uiRecordedNote.RectTransform, uiRecordedNote.MidiNote, uiRecordedNote.StartBeat, uiRecordedNote.EndBeat);
            }
        }
    }

    public void OnInjectionFinished()
    {
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
            throw new UnityException("SentenceDisplayer must be initialized with a row count >= 12 (one row for each note in an octave)");
        }

        noteHeightPercent = 1.0f / noteRowCount;
    }

    public void RemoveAllDisplayedNotes()
    {
        RemoveUiNotes();
        RemoveUiRecordedNotes();
    }

    public void DisplaySentence(Sentence sentence)
    {
        displayedSentence = sentence;
        RemoveAllDisplayedNotes();
        if (sentence == null)
        {
            return;
        }

        avgMidiNote = displayedSentence.Notes.Count > 0
            ? (int)displayedSentence.Notes.Select(it => it.MidiNote).Average()
            : 0;
        // The division is rounded down on purpose (e.g. noteRowCount of 3 will result in (noteRowCount / 2) == 1)
        maxNoteRowMidiNote = avgMidiNote + (noteRowCount / 2);
        minNoteRowMidiNote = avgMidiNote - (noteRowCount / 2);
        // Freestyle notes are not drawn
        IEnumerable<Note> nonFreestyleNotes = sentence.Notes.Where(note => !note.IsFreestyle);
        foreach (Note note in nonFreestyleNotes)
        {
            CreateUiNote(note);
        }
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

    private void RemoveUiNotes()
    {
        foreach (Transform child in uiNotesContainer.transform)
        {
            Destroy(child.gameObject);
        }
        noteToUiNoteMap.Clear();
    }

    private void CreateUiNote(Note note)
    {
        if (note.StartBeat == note.EndBeat)
        {
            return;
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

    private void RemoveUiRecordedNotes()
    {
        foreach (Transform child in uiRecordedNotesContainer.transform)
        {
            Destroy(child.gameObject);
        }
        uiRecordedNotes.Clear();
        recordedNoteToUiRecordedNotesMap.Clear();
    }

    private void CreateUiRecordedNote(RecordedNote recordedNote, bool useRoundedMidiNote)
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
            int relativeSignedDistance = MidiUtils.GetRelativePitchDistanceSigned(recordedNote.TargetNote.MidiNote, recordedNote.RoundedMidiNote);
            int roundedMidiNoteOnOctaveOfTargetNote = recordedNote.TargetNote.MidiNote + relativeSignedDistance;
            if (MidiUtils.GetRelativePitch(recordedNote.RoundedMidiNote) != MidiUtils.GetRelativePitch(roundedMidiNoteOnOctaveOfTargetNote))
            {
                // Should never happen
                Debug.LogError($"The displayed midi note does not correspond to the rounded recorded midi note:"
                    + $"recorded {recordedNote.RoundedMidiNote}, target: {recordedNote.TargetNote.MidiNote}, displayed: {roundedMidiNoteOnOctaveOfTargetNote}");
            }
            midiNote = roundedMidiNoteOnOctaveOfTargetNote;
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

    private void PositionUiNote(RectTransform uiNote, int midiNote, double noteStartBeat, double noteEndBeat)
    {
        int noteRow = CalculateNoteRow(midiNote);

        int sentenceStartBeat = displayedSentence.MinBeat;
        int sentenceEndBeat = displayedSentence.MaxBeat;
        int beatsInSentence = sentenceEndBeat - sentenceStartBeat;

        float anchorY = (float)noteRow / noteRowCount;
        float anchorYStart = anchorY - noteHeightPercent;
        float anchorYEnd = anchorY + noteHeightPercent;
        float anchorXStart = (float)(noteStartBeat - sentenceStartBeat) / beatsInSentence;
        float anchorXEnd = (float)(noteEndBeat - sentenceStartBeat) / beatsInSentence;

        uiNote.anchorMin = new Vector2(anchorXStart, anchorYStart);
        uiNote.anchorMax = new Vector2(anchorXEnd, anchorYEnd);
        uiNote.MoveCornersToAnchors();
    }

    public void CreatePerfectSentenceEffect()
    {
        for (int i = 0; i < 50; i++)
        {
            CreatePerfectSentenceStar();
        }
    }

    private void CreatePerfectSentenceStar()
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

    private int CalculateNoteRow(int midiNote)
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
}
