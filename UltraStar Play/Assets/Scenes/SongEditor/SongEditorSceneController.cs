﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UniInject;
using UnityEngine;
using UnityEngine.UI;

// Disable warning about fields that are never assigned, their values are injected.
#pragma warning disable CS0649

public class SongEditorSceneController : MonoBehaviour, IBinder, INeedInjection
{
    [InjectedInInspector]
    public string defaultSongName;

    [TextArea(3, 8)]
    [Tooltip("Convenience text field to paste and copy song names when debugging.")]
    public string defaultSongNamePasteBin;

    [InjectedInInspector]
    public SongAudioPlayer songAudioPlayer;

    [InjectedInInspector]
    public SongVideoPlayer songVideoPlayer;

    [InjectedInInspector]
    public SongEditorNoteRecorder songEditorNoteRecorder;

    [InjectedInInspector]
    public SongEditorSelectionController selectionController;

    [InjectedInInspector]
    public RectTransform uiNoteContainer;

    [InjectedInInspector]
    public AudioWaveFormVisualizer audioWaveFormVisualizer;

    [InjectedInInspector]
    public NoteArea noteArea;

    [InjectedInInspector]
    public NoteAreaDragHandler noteAreaDragHandler;

    [InjectedInInspector]
    public EditorNoteDisplayer editorNoteDisplayer;

    [InjectedInInspector]
    public MicrophonePitchTracker microphonePitchTracker;

    [InjectedInInspector]
    public Canvas canvas;

    [InjectedInInspector]
    public GraphicRaycaster graphicRaycaster;

    [Inject]
    private Injector injector;

    private bool lastIsPlaying;
    private double positionInSongInMillisWhenPlaybackStarted;

    private Dictionary<Voice, Color> voiceToColorMap = new Dictionary<Voice, Color>();

    private readonly SongEditorLayerManager songEditorLayerManager = new SongEditorLayerManager();

    private bool audioWaveFormInitialized;

    public SongMeta SongMeta
    {
        get
        {
            return SceneData.SelectedSongMeta;
        }
    }

    private Dictionary<string, Voice> voiceIdToVoiceMap;
    private Dictionary<string, Voice> VoiceIdToVoiceMap
    {
        get
        {
            if (voiceIdToVoiceMap == null)
            {
                voiceIdToVoiceMap = SongMetaManager.GetVoices(SongMeta);
            }
            return voiceIdToVoiceMap;
        }
    }

    public List<Voice> Voices
    {
        get
        {
            return VoiceIdToVoiceMap.Values.ToList();
        }
    }

    private SongEditorSceneData sceneData;
    public SongEditorSceneData SceneData
    {
        get
        {
            if (sceneData == null)
            {
                sceneData = SceneNavigator.Instance.GetSceneData<SongEditorSceneData>(CreateDefaultSceneData());
            }
            return sceneData;
        }
    }

    public List<IBinding> GetBindings()
    {
        BindingBuilder bb = new BindingBuilder();
        // Note that the SceneData and SongMeta are loaded on access here if not done yet.
        bb.BindExistingInstance(SceneData);
        bb.BindExistingInstance(SongMeta);
        bb.BindExistingInstance(songAudioPlayer);
        bb.BindExistingInstance(songVideoPlayer);
        bb.BindExistingInstance(noteArea);
        bb.BindExistingInstance(noteAreaDragHandler);
        bb.BindExistingInstance(songEditorLayerManager);
        bb.BindExistingInstance(microphonePitchTracker);
        bb.BindExistingInstance(songEditorNoteRecorder);
        bb.BindExistingInstance(selectionController);
        bb.BindExistingInstance(editorNoteDisplayer);
        bb.BindExistingInstance(canvas);
        bb.BindExistingInstance(graphicRaycaster);
        bb.BindExistingInstance(this);
        return bb.GetBindings();
    }

    void Awake()
    {
        Debug.Log($"Start editing of '{SceneData.SelectedSongMeta.Title}' at {SceneData.PositionInSongInMillis} ms.");
        songAudioPlayer.Init(SongMeta);
        songVideoPlayer.Init(SongMeta, songAudioPlayer);

        songAudioPlayer.PositionInSongInMillis = SceneData.PositionInSongInMillis;
    }

    void Update()
    {
        // Jump to last position in song when playback stops
        if (songAudioPlayer.IsPlaying)
        {
            if (!lastIsPlaying)
            {
                positionInSongInMillisWhenPlaybackStarted = songAudioPlayer.PositionInSongInMillis;
            }
        }
        else
        {
            if (lastIsPlaying)
            {
                songAudioPlayer.PositionInSongInMillis = positionInSongInMillisWhenPlaybackStarted;
            }
        }
        lastIsPlaying = songAudioPlayer.IsPlaying;

        // Create the audio waveform image if not done yet.
        if (!audioWaveFormInitialized && songAudioPlayer.HasAudioClip && songAudioPlayer.AudioClip.samples > 0)
        {
            using (new DisposableStopwatch($"Created audio waveform in <millis> ms"))
            {
                audioWaveFormInitialized = true;
                audioWaveFormVisualizer.DrawWaveFormMinAndMaxValues(songAudioPlayer.AudioClip);
            }
        }
    }

    public Color GetColorForVoice(Voice voice)
    {
        if (voiceToColorMap.TryGetValue(voice, out Color color))
        {
            return color;
        }
        else
        {
            // Define colors for the voices.
            CreateVoiceToColorMap();
            return voiceToColorMap[voice];
        }
    }

    private void CreateVoiceToColorMap()
    {
        List<Color> colors = new List<Color> { Colors.beige, Colors.crimson, Colors.forestGreen, Colors.dodgerBlue,
                Colors.gold, Colors.greenYellow, Colors.salmon, Colors.violet };
        List<Voice> sortedVoices = new List<Voice>(Voices);
        sortedVoices.Sort(Voice.comparerByName);
        int index = 0;
        foreach (Voice v in sortedVoices)
        {
            voiceToColorMap[v] = colors[index];
            index++;
        }
    }

    // Returns the notes in the song as well as the notes in the layers in no particular order.
    public List<Note> GetAllNotes()
    {
        List<Note> result = new List<Note>();
        List<Note> notesInVoices = GetAllSentences().SelectMany(sentence => sentence.Notes).ToList();
        List<Note> notesInLayers = songEditorLayerManager.GetAllNotes();
        result.AddRange(notesInVoices);
        result.AddRange(notesInLayers);
        return result;
    }

    public List<Sentence> GetAllSentences()
    {
        List<Sentence> result = new List<Sentence>();
        List<Voice> voices = VoiceIdToVoiceMap.Values.ToList();
        List<Sentence> sentencesInVoices = voices.SelectMany(voice => voice.Sentences).ToList();
        List<Note> notesInLayers = songEditorLayerManager.GetAllNotes();
        result.AddRange(sentencesInVoices);
        return result;
    }

    public Sentence GetNextSentence(Sentence sentence)
    {
        List<Sentence> sentencesOfVoice = VoiceIdToVoiceMap.Values.Where(voice => voice == sentence.Voice)
            .SelectMany(voiceIdToVoiceMap => voiceIdToVoiceMap.Sentences).ToList();
        sentencesOfVoice.Sort(Sentence.comparerByStartBeat);
        Sentence lastSentence = null;
        foreach (Sentence s in sentencesOfVoice)
        {
            if (lastSentence == sentence)
            {
                return s;
            }
            lastSentence = s;
        }
        return null;
    }

    public Sentence GetPreviousSentence(Sentence sentence)
    {
        List<Sentence> sentencesOfVoice = VoiceIdToVoiceMap.Values.Where(voice => voice == sentence.Voice)
            .SelectMany(voiceIdToVoiceMap => voiceIdToVoiceMap.Sentences).ToList();
        sentencesOfVoice.Sort(Sentence.comparerByStartBeat);
        Sentence lastSentence = null;
        foreach (Sentence s in sentencesOfVoice)
        {
            if (s == sentence)
            {
                return lastSentence;
            }
            lastSentence = s;
        }
        return null;
    }

    public Voice GetOrCreateVoice(int index)
    {
        List<Voice> sortedVoices = new List<Voice>(Voices);
        sortedVoices.Sort(Voice.comparerByName);
        if (sortedVoices.Count <= index)
        {
            // Set voice identifier for solo voice because this is not a solo song anymore.
            if (sortedVoices.Count > 0 && sortedVoices[0].Name.IsNullOrEmpty())
            {
                sortedVoices[0].SetName("P1");
            }

            // Create all missing voices up to the index
            for (int i = sortedVoices.Count; i <= index; i++)
            {
                string voiceIdentifier = "P" + (i + 1);
                Voice newVoice = new Voice(new List<Sentence>(), voiceIdentifier);
                VoiceIdToVoiceMap.Add(voiceIdentifier, newVoice);
                sortedVoices.Add(newVoice);

                Debug.Log($"Created new voice: {voiceIdentifier}");
            }
            OnNotesChanged();
        }
        return sortedVoices[index];
    }

    public void OnNotesChanged()
    {
        // TODO: Create history for undo/redo
        editorNoteDisplayer.ReloadSentences();
        editorNoteDisplayer.UpdateNotesAndSentences();
    }

    public void DeleteNote(Note note)
    {
        note.SetSentence(null);
        songEditorLayerManager.RemoveNoteFromAllLayers(note);
        editorNoteDisplayer.DeleteNote(note);
    }

    public void DeleteNotes(IReadOnlyCollection<Note> notes)
    {
        foreach (Note note in new List<Note>(notes))
        {
            DeleteNote(note);
        }
    }

    public void DeleteSentence(Sentence sentence)
    {
        DeleteNotes(sentence.Notes);
        sentence.SetVoice(null);
        editorNoteDisplayer.ReloadSentences();
    }

    public void TogglePlayPause()
    {
        if (songAudioPlayer.IsPlaying)
        {
            songAudioPlayer.PauseAudio();
        }
        else
        {
            songAudioPlayer.PlayAudio();
        }
    }

    public void OnBackButtonClicked()
    {
        ContinueToSingScene();
    }

    public void OnSaveButtonClicked()
    {
        SaveSong();
    }

    private void SaveSong()
    {
        // TODO: Implement saving the song file.
        // TODO: A backup of the original file should be created (copy original txt file, but only once),
        // to avoid breaking songs because of issues in loading / saving the song data.
        // (This project is still in early development and untested and should not break songs of the users.)
    }

    private void ContinueToSingScene()
    {
        if (sceneData.PreviousSceneData is SingSceneData)
        {
            SingSceneData singSceneData = sceneData.PreviousSceneData as SingSceneData;
            singSceneData.PositionInSongInMillis = songAudioPlayer.PositionInSongInMillis;
        }
        SceneNavigator.Instance.LoadScene(sceneData.PreviousScene, sceneData.PreviousSceneData);
    }

    private SongEditorSceneData CreateDefaultSceneData()
    {
        SongEditorSceneData defaultSceneData = new SongEditorSceneData();
        defaultSceneData.PositionInSongInMillis = 0;
        defaultSceneData.SelectedSongMeta = SongMetaManager.Instance.FindSongMeta(defaultSongName);

        // Set up PreviousSceneData to directly start the SingScene.
        defaultSceneData.PreviousScene = EScene.SingScene;

        SingSceneData singSceneData = new SingSceneData();

        PlayerProfile playerProfile = SettingsManager.Instance.Settings.PlayerProfiles[0];
        List<PlayerProfile> playerProfiles = new List<PlayerProfile>();
        playerProfiles.Add(playerProfile);
        singSceneData.SelectedPlayerProfiles = playerProfiles;

        defaultSceneData.PreviousSceneData = singSceneData;

        return defaultSceneData;
    }
}
