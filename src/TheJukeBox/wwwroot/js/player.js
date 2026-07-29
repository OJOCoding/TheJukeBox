let currentPlaylist = [];
let currentTrackIndex = -1;
let audioPlayer = null;
let isShuffle = false;
let isRepeat = false;
let shuffledPlaylist = [];
let hasUserSelectedTrack = false; // Track if user has selected a track

// Initialize when DOM is loaded
document.addEventListener('DOMContentLoaded', function () {
    audioPlayer = document.getElementById('audioPlayer');

    if (!audioPlayer) {
        console.error('Audio player element not found!');
        return;
    }

    // Set initial state
    resetPlayerState();

    audioPlayer.addEventListener('ended', handleTrackEnd);
    audioPlayer.addEventListener('error', function (e) {
        // Only show error if user has actually selected a track
        if (hasUserSelectedTrack && audioPlayer.src) {
            console.error('Audio element error:', e);
            showError('Audio playback error. The file format may not be supported.');
        }
        // If no track is selected, ignore the error silently
    });

    loadMusicLibrary();
});

// Reset player to initial state
function resetPlayerState() {
    updateTrackTitle('SELECT TRACK TO BEGIN');
    document.getElementById('currentTrackInfo').textContent = 'CLICK ANY SONG FROM PLAYLIST';
    document.getElementById('currentTrack').textContent = '-';
    updatePlayButtonState(false);
    hasUserSelectedTrack = false; // Reset user selection flag

    // Clear audio source
    if (audioPlayer) {
        audioPlayer.pause();
        audioPlayer.currentTime = 0;
        audioPlayer.src = '';
        audioPlayer.load(); // Clear any pending loads
    }
}

// Update track title
function updateTrackTitle(title) {
    const trackNameElement = document.getElementById('currentTrackName');
    if (trackNameElement) {
        trackNameElement.textContent = title;
    }
}

// Update play button state
function updatePlayButtonState(isEnabled) {
    const playPauseBtn = document.getElementById('playPauseBtn');
    if (playPauseBtn) {
        if (isEnabled) {
            playPauseBtn.disabled = false;
            playPauseBtn.style.opacity = '1';
            playPauseBtn.style.cursor = 'pointer';
        } else {
            playPauseBtn.disabled = true;
            playPauseBtn.style.opacity = '0.5';
            playPauseBtn.style.cursor = 'not-allowed';
        }
    }
}

window.playPause = function () {
    if (!audioPlayer) {
        console.error('Audio player not initialized');
        showError('Audio player not ready. Please refresh the page.');
        return;
    }

    if (!audioPlayer.src || currentTrackIndex === -1) {
        showError('Please select a track first by clicking on a song in the playlist.');
        return;
    }

    try {
        if (audioPlayer.paused) {
            const playPromise = audioPlayer.play();
            if (playPromise !== undefined) {
                playPromise.catch(error => {
                    console.error('Play failed:', error);
                    if (error.name !== 'AbortError') {
                        showError('Play failed: ' + error.message);
                    }
                });
            }
        } else {
            audioPlayer.pause();
        }
    } catch (error) {
        console.error('Play/Pause error:', error);
        showError('Audio error: ' + error.message);
    }
};

window.playNext = function () {
    if (currentPlaylist.length === 0 || currentTrackIndex === -1) {
        showError('Please select a track first.');
        return;
    }

    let nextIndex;
    if (isShuffle) {
        const currentShuffledIndex = shuffledPlaylist.indexOf(currentTrackIndex);
        nextIndex = shuffledPlaylist[(currentShuffledIndex + 1) % shuffledPlaylist.length];
    } else {
        nextIndex = (currentTrackIndex + 1) % currentPlaylist.length;
    }

    playTrack(nextIndex);
};

window.playPrevious = function () {
    if (currentPlaylist.length === 0 || currentTrackIndex === -1) {
        showError('Please select a track first.');
        return;
    }

    let prevIndex;
    if (isShuffle) {
        const currentShuffledIndex = shuffledPlaylist.indexOf(currentTrackIndex);
        prevIndex = shuffledPlaylist[(currentShuffledIndex - 1 + shuffledPlaylist.length) % shuffledPlaylist.length];
    } else {
        prevIndex = (currentTrackIndex - 1 + currentPlaylist.length) % currentPlaylist.length;
    }

    playTrack(prevIndex);
};

window.refreshLibrary = function () {
    showLoading('Refreshing library...');
    currentTrackIndex = -1;
    resetPlayerState();
    loadMusicLibrary();
};

// Shuffle functionality
window.toggleShuffle = function () {
    isShuffle = !isShuffle;
    const shuffleBtn = document.getElementById('shuffleBtn');

    if (shuffleBtn) {
        if (isShuffle) {
            shuffleBtn.style.background = '#000000';
            shuffleBtn.style.color = '#ffffff';
            shuffledPlaylist = [...Array(currentPlaylist.length).keys()];
            shuffleArray(shuffledPlaylist);
        } else {
            shuffleBtn.style.background = '#ffffff';
            shuffleBtn.style.color = '#000000';
        }
    }
};

// Repeat functionality
window.toggleRepeat = function () {
    isRepeat = !isRepeat;
    const repeatBtn = document.getElementById('repeatBtn');

    if (repeatBtn) {
        if (isRepeat) {
            repeatBtn.style.background = '#000000';
            repeatBtn.style.color = '#ffffff';
            repeatBtn.textContent = '🔁 REPEAT ON';
        } else {
            repeatBtn.style.background = '#ffffff';
            repeatBtn.style.color = '#000000';
            repeatBtn.textContent = '🔁 REPEAT';
        }
    }
};

// Handle track end
function handleTrackEnd() {
    if (isRepeat) {
        audioPlayer.currentTime = 0;
        audioPlayer.play().catch(console.error);
    } else {
        playNext();
    }
}

// Fisher-Yates shuffle algorithm
function shuffleArray(array) {
    for (let i = array.length - 1; i > 0; i--) {
        const j = Math.floor(Math.random() * (i + 1));
        [array[i], array[j]] = [array[j], array[i]];
    }
    return array;
}

function playTrack(index) {
    if (!audioPlayer) {
        console.error('Audio player not initialized');
        return;
    }

    if (index < 0 || index >= currentPlaylist.length) return;

    const track = currentPlaylist[index];

    // First select the track
    selectTrack(index);

    // Use our API stream URL
    const audioUrl = track.stream_url;

    console.log('Playing track:', track.name, 'Stream URL:', audioUrl);

    // Stop current playback and reset
    audioPlayer.pause();
    audioPlayer.currentTime = 0;

    // Clear previous source
    audioPlayer.src = '';

    // Set the new source
    audioPlayer.src = audioUrl;

    // Set the audio type for better browser support
    const extension = track.extension?.toLowerCase();
    if (extension) {
        const type = getAudioType(extension);
        if (type) {
            console.log('Setting audio type:', type);
        }
    }

    // Load the new source
    audioPlayer.load();

    // Mark that user has selected a track (so we show future errors)
    hasUserSelectedTrack = true;

    // Enable play button
    updatePlayButtonState(true);

    // Try to play
    const playPromise = audioPlayer.play();

    if (playPromise !== undefined) {
        playPromise.then(() => {
            console.log('Audio started playing successfully');
        }).catch(error => {
            console.error('Play failed:', error);
            if (error.name !== 'AbortError' && hasUserSelectedTrack) {
                showError('Cannot play audio: ' + error.message);
            }
        });
    }
}

function getAudioType(extension) {
    const types = {
        '.mp3': 'audio/mpeg',
        '.wav': 'audio/wav',
        '.ogg': 'audio/ogg',
        '.flac': 'audio/flac',
        '.m4a': 'audio/mp4',
        '.aac': 'audio/aac',
        '.mp4': 'audio/mp4'
    };
    return types[extension] || 'audio/mpeg';
}

function selectTrack(index) {
    if (index < 0 || index >= currentPlaylist.length) return;

    currentTrackIndex = index;
    const track = currentPlaylist[index];

    // Clean the type string
    const cleanType = track.type.replace(/[^\x20-\x7E]/g, '');

    // Update UI with actual track info
    updateTrackTitle(track.name);
    document.getElementById('currentTrackInfo').textContent =
        `${getFileTypeDisplay(cleanType)} - ${track.size}`;
    document.getElementById('currentTrack').textContent = (index + 1);

    // Enable play button
    updatePlayButtonState(true);

    // Mark that user has selected a track
    hasUserSelectedTrack = true;

    displayPlaylist();
}

function getFileTypeDisplay(mimeType) {
    const typeMap = {
        'audio/mpeg': 'MP3',
        'audio/wav': 'WAV',
        'audio/ogg': 'OGG',
        'audio/flac': 'FLAC',
        'audio/mp4': 'M4A',
        'audio/aac': 'AAC',
        'audio/x-m4a': 'M4A'
    };
    return typeMap[mimeType] || 'AUDIO';
}

async function loadMusicLibrary() {
    try {
        showLoading('Loading your music library...');
        const response = await fetch('/api/audiofiles');

        if (!response.ok) {
            throw new Error(`HTTP error! status: ${response.status}`);
        }

        const data = await response.json();

        if (data.success) {
            currentPlaylist = data.audio_files || [];
            displayPlaylist();
            updateStats();

            if (currentPlaylist.length > 0) {
                // Initialize shuffle playlist but don't select any track
                shuffledPlaylist = [...Array(currentPlaylist.length).keys()];
                console.log('Library loaded. Please select a track to begin playback.');
            } else {
                showError('No audio files found in your Google Drive');
                updateTrackTitle('NO TRACKS AVAILABLE');
                updatePlayButtonState(false);
            }
        } else {
            showError('Failed to load music library: ' + (data.error || 'Unknown error'));
            updateTrackTitle('LOADING ERROR');
            updatePlayButtonState(false);
        }
    } catch (error) {
        console.error('Load music error:', error);
        showError('Error loading music: ' + error.message);
        updateTrackTitle('LOADING ERROR');
        updatePlayButtonState(false);
    }
}

function displayPlaylist() {
    const playlistElement = document.getElementById('playlist');

    if (!playlistElement) {
        console.error('Playlist element not found!');
        return;
    }

    if (currentPlaylist.length === 0) {
        playlistElement.innerHTML = '<div class="error">No audio files found in your Google Drive</div>';
        return;
    }

    playlistElement.innerHTML = currentPlaylist.map((track, index) => `
        <div class="track-item ${index === currentTrackIndex ? 'active' : ''}"
            onclick="playTrack(${index})">
            <div class="track-name">${escapeHtml(track.name)}</div>
            <div class="track-duration">${escapeHtml(track.size)}</div>
        </div>
    `).join('');
}

function updateStats() {
    const totalSongsElement = document.getElementById('totalSongs');
    if (totalSongsElement) {
        totalSongsElement.textContent = currentPlaylist.length;
    }
}

function showLoading(message) {
    const playlistElement = document.getElementById('playlist');
    if (playlistElement) {
        playlistElement.innerHTML = `<div class="loading">${message}</div>`;
    }
}

function showError(message) {
    const playlistElement = document.getElementById('playlist');
    if (playlistElement) {
        playlistElement.innerHTML = `<div class='error'>${message}</div>`;
    }

    const trackInfo = document.getElementById('currentTrackInfo');
    if (trackInfo) {
        trackInfo.textContent = 'Error: ' + message;
        trackInfo.style.color = '#ff0000';
    }
}

function escapeHtml(unsafe) {
    if (!unsafe) return '';
    return unsafe
        .toString()
        .replace(/&/g, "&amp;")
        .replace(/</g, "&lt;")
        .replace(/>/g, "&gt;")
        .replace(/"/g, "&quot;")
        .replace(/'/g, "&#039;");
}

// Keyboard controls
document.addEventListener('keydown', function (event) {
    if (event.target.tagName === 'INPUT' || event.target.tagName === 'TEXTAREA') {
        return;
    }

    switch (event.code) {
        case 'Space':
            event.preventDefault();
            playPause();
            break;
        case 'ArrowRight':
            event.preventDefault();
            playNext();
            break;
        case 'ArrowLeft':
            event.preventDefault();
            playPrevious();
            break;
        case 'KeyS':
            event.preventDefault();
            toggleShuffle();
            break;
        case 'KeyR':
            event.preventDefault();
            toggleRepeat();
            break;
    }
});