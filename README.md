# JukeBox - Google Drive Music Player

A professional web application that transforms your Google Drive into a personal music streaming service. Built with ASP.NET Core and featuring a technical blueprint-inspired interface.

# Overview
JukeBox provides a seamless interface for streaming audio files directly from your Google Drive storage. The application features a unique monochrome design with technical blueprint aesthetics, offering both functionality and visual appeal.

# Features
- Cloud Music Streaming - Directly play audio files from Google Drive storage

- Automatic Library Synchronization - Real-time detection of audio files in your Drive

- Comprehensive Player Controls - Play, pause, next, previous, shuffle, and repeat functionality

- Responsive Design - Optimized for both desktop and mobile devices

- Technical Blueprint Interface - Professional monochrome design with blueprint-inspired aesthetics

- High Performance - Built with modern web technologies for optimal efficiency

# Quick Start Guide
# Prerequisites
- .NET 8.0 SDK

- Google Cloud Platform account

- Google Drive account with audio files

# Installation Steps

- Clone the Repository

``` 
git clone https://github.com/yourusername/jukebox.git
cd jukebox
```

# Configure Google Cloud Platform

- Navigate to Google Cloud Console

- Create a new project or select an existing one

- Enable the Google Drive API

- Create OAuth 2.0 credentials (Desktop application type)

- Add authorized redirect URI: https://localhost:7077/api/auth/callback

# Application Configuration

-  Place your credentials.json file in the AUTH directory

-  Verify the folder structure matches:

```
text
TheJukeBox/
├── AUTH/
│   └── credentials.json
├── CLIENT/
│   ├── CSS/
│   ├── JS/
│   ├── index.html
│   └── player.html
```

# Launch Application

```dotnet run```

Access the application at https://localhost:7077

# Initial Setup Procedure

- Open https://localhost:7077 in your web browser

- Select "INITIATE PLAYER"

- Complete Google account authentication

- Grant necessary permissions for Google Drive access

- Begin music playback from your library

# User Guide

# Basic Operation

- Track Selection - Click any track in the playlist to select and begin playback

- Spacebar - Toggle play/pause

- Arrow Left - Navigate to previous track

- Arrow Right - Navigate to next track

- S Key - Activate/deactivate shuffle mode

- R Key - Activate/deactivate repeat mode

# Player Functionality

- Shuffle Mode - Randomizes track playback sequence

- Repeat Mode - Continuously loops current track

- Dynamic Library Updates - Refresh to access newly added files

- Comprehensive Metadata - Displays file format, size, and technical information

# Technical Specifications
- System Architecture
```
Frontend Layer (CLIENT/)     Backend Layer (ASP.NET Core API)
├── HTML/CSS/JavaScript      ├── Controller Management
├── Technical UI Interface   ├── Service Layer
└── Responsive Framework     └── Google Drive API Integration
```

# Supported Audio Formats

- MP3 (.mp3)
- WAV (.wav)
- M4A (.m4a)
- FLAC (.flac)
- OGG (.ogg)
- AAC (.aac)

# API Endpoints

```bash 
GET  /api/audiofiles                 # Retrieve all audio files
GET  /api/audiofiles/search          # Search files by filename
GET  /api/audiofiles/download/{id}   # Stream audio content
GET  /api/auth/setup                 # OAuth configuration
GET  /api/auth/callback              # OAuth authentication handler
```
# Development Information
Project Architecture
```
TheJukeBox/
├── API/
│   ├── HomeController.cs
│   ├── PlayerController.cs
│   ├── AudioFilesController.cs
│   └── AuthController.cs
│   ├── IGoogleAuthService.cs
│   ├── GoogleAPI_Service.cs
│   └── GoogleDriveService.cs
├── CORE/ GoogleAuthConfig.cs
├── CLIENT/
│   ├── CSS/ (Technical-styled CSS)
│   ├── JS/ (Player functionality)
│   └── HTML Pages (Frontend interfaces)
└── AUTH/ (OAuth configuration files)
```

# Build Instructions
## Restore project dependencies
dotnet restore
## Compile project
dotnet build
## Execute with development configuration
dotnet run --environment Development
## Execute with production configuration  
dotnet run --environment Production

# Security and Privacy

- OAuth 2.0 Protocol - Secure Google account integration

- Read-Only Permissions - Application accesses audio files in read-only mode

- Local Token Management - Authentication tokens stored locally on client machine

- Zero Data Collection - No user information transmitted to external servers

# Deployment Options
## Self-Hosted Deployment
```bash
dotnet publish -c Release -o ./publish
cd publish
dotnet TheJukeBox.dll
```

## Container Deployment
```
dockerfile
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY ./publish .
ENTRYPOINT ["dotnet", "TheJukeBox.dll"]
```

# License
This project operates under the MIT License. Refer to the LICENSE file for complete details.

# Acknowledgments

- Google Drive API for cloud storage integration
- ASP.NET Core framework for backend infrastructure
- Modern web standards ensuring cross-browser compatibility