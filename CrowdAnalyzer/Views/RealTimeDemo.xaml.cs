﻿// 
// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license.
// 
// Microsoft Cognitive Services: http://www.microsoft.com/cognitive
// 
// Microsoft Cognitive Services Github:
// https://github.com/Microsoft/Cognitive
// 
// Copyright (c) Microsoft Corporation
// All rights reserved.
// 
// MIT License:
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
// 
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED ""AS IS"", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
// 

using IntelligentKioskSample.Controls;
using Microsoft.Azure.CognitiveServices.Vision.Face.Models;
using ServiceHelpers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Serialization;
using Windows.Graphics.Imaging;
using Windows.UI.Popups;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

// The Blank Page item template is documented at http://go.microsoft.com/fwlink/?LinkId=234238

namespace IntelligentKioskSample.Views
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    [KioskExperience(Title = "Realtime Crowd Insights", ImagePath = "ms-appx:/Assets/realtime.png", ExperienceType = ExperienceType.Kiosk)]
    public sealed partial class RealTimeDemo : Page, IRealTimeDataProvider
    {
        private Task processingLoopTask;
        private bool isProcessingLoopInProgress;
        private bool isProcessingPhoto;

        private DateTime lastResultsTimestamp = DateTime.MinValue;
        private IEnumerable<DetectedFace> lastDetectedFaceSample;
        private IEnumerable<Tuple<DetectedFace, IdentifiedPerson>> lastIdentifiedPersonSample;
        private IEnumerable<SimilarFaceMatch> lastSimilarPersistedFaceSample;

        private DemographicsData demographics;
        private Dictionary<Guid, Visitor> visitors = new Dictionary<Guid, Visitor>();

        public static bool ShowAgeAndGender { get { return SettingsHelper.Instance.ShowAgeAndGender; } }

        public List<string> outputMessages = new List<string>();

        private int NrOfPeopleAtDisplay
        {
            set
            {
                SpanNrOfPeopleNow.Text = value.ToString();
            }
        }

        private int NrOfPeopleAtDisplayHistory
        {
            set
            {
                SpanNrOfPeopleHistory.Text = value.ToString();
            }
        }

        private DateTime timeOfLastLog = DateTime.Now;

        public void MaybeNewOutputMessage(string message, bool force = false)
        {
            if ((DateTime.Now - timeOfLastLog).Seconds >= 5 || force)
            {
                outputMessages.Add($"{DateTime.Now.ToLongTimeString()} {message}");
                string totalMessage = string.Join('\n', outputMessages);

                debugText.Text = totalMessage;
                timeOfLastLog = DateTime.Now;
            }
        }

        public RealTimeDemo()
        {
            this.InitializeComponent();

            this.DataContext = this;

            Window.Current.Activated += CurrentWindowActivationStateChanged;
            this.cameraControl.SetRealTimeDataProvider(this);
            this.cameraControl.FilterOutSmallFaces = true;
            this.cameraControl.HideCameraControls();
            this.cameraControl.CameraAspectRatioChanged += CameraControl_CameraAspectRatioChanged;
        }

        private void CameraControl_CameraAspectRatioChanged(object sender, EventArgs e)
        {
            this.UpdateCameraHostSize();
        }

        private void StartProcessingLoop()
        {
            this.isProcessingLoopInProgress = true;

            if (this.processingLoopTask == null || this.processingLoopTask.Status != TaskStatus.Running)
            {
                this.processingLoopTask = Task.Run(() => this.ProcessingLoop());
            }
        }


        private async void ProcessingLoop()
        {
            while (this.isProcessingLoopInProgress)
            {
                await this.Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, async () =>
                {
                    if (!this.isProcessingPhoto)
                    {
                        if (DateTime.Now.Hour != this.demographics.StartTime.Hour)
                        {
                            // We have been running through the hour. Reset the data...
                            await this.ResetDemographicsData();
                            this.UpdateDemographicsUI();
                        }

                        this.isProcessingPhoto = true;
                        if (this.cameraControl.NumFacesOnLastFrame == 0)
                        {
                            await this.ProcessCameraCapture(null);
                        }
                        else
                        {
                            await this.ProcessCameraCapture(await this.cameraControl.CaptureFrameAsync());
                        }
                    }
                });

                await Task.Delay(1000);
            }
        }

        private async void CurrentWindowActivationStateChanged(object sender, Windows.UI.Core.WindowActivatedEventArgs e)
        {
            if ((e.WindowActivationState == Windows.UI.Core.CoreWindowActivationState.CodeActivated ||
                e.WindowActivationState == Windows.UI.Core.CoreWindowActivationState.PointerActivated) &&
                this.cameraControl.CameraStreamState == Windows.Media.Devices.CameraStreamState.Shutdown)
            {
                // When our Window loses focus due to user interaction Windows shuts it down, so we 
                // detect here when the window regains focus and trigger a restart of the camera.
                await this.cameraControl.StartStreamAsync(isForRealTimeProcessing: true);
            }
        }

        private async Task ProcessCameraCapture(ImageAnalyzer e)
        {
            if (e == null)
            {
                this.lastDetectedFaceSample = null;
                this.lastIdentifiedPersonSample = null;
                this.lastSimilarPersistedFaceSample = null;

                this.isProcessingPhoto = false;
                NrOfPeopleAtDisplay = 0;
                MaybeNewOutputMessage("Nobody at display...");
                return;
            }

            DateTime start = DateTime.Now;

            // Compute Emotion, Age and Gender
            await this.DetectFaceAttributesAsync(e);

            // Compute Face Identification and Unique Face Ids
            await Task.WhenAll(ComputeFaceIdentificationAsync(e), this.ComputeUniqueFaceIdAsync(e));

            lastResultsTimestamp = DateTime.Now;

            this.UpdateDemographics(e);
            this.UpdateEmotionTimelineUI(e);

            this.isProcessingPhoto = false;
        }

        private async Task ComputeUniqueFaceIdAsync(ImageAnalyzer e)
        {
            await e.FindSimilarPersistedFacesAsync();

            if (!e.SimilarFaceMatches.Any())
            {
                this.lastSimilarPersistedFaceSample = null;
            }
            else
            {
                this.lastSimilarPersistedFaceSample = e.SimilarFaceMatches;
            }
        }

        private async Task ComputeFaceIdentificationAsync(ImageAnalyzer e)
        {
            await e.IdentifyFacesAsync();

            if (!e.IdentifiedPersons.Any())
            {
                this.lastIdentifiedPersonSample = null;
            }
            else
            {
                this.lastIdentifiedPersonSample = e.DetectedFaces.Select(f => new Tuple<DetectedFace, IdentifiedPerson>(f, e.IdentifiedPersons.FirstOrDefault(p => p.FaceId == f.FaceId)));
            }
        }

        private async Task DetectFaceAttributesAsync(ImageAnalyzer e)
        {
            await e.DetectFacesAsync(detectFaceAttributes: true);

            if (e.DetectedFaces == null || !e.DetectedFaces.Any())
            {
                this.lastDetectedFaceSample = null;
            }
            else
            {
                this.lastDetectedFaceSample = e.DetectedFaces;
            }
        }

        private void UpdateEmotionTimelineUI(ImageAnalyzer e)
        {
            if (!e.DetectedFaces.Any())
            {
                this.ShowTimelineFeedbackForNoFaces();
            }
            else
            {
                Emotion averageScores = new Emotion
                {
                    Happiness = e.DetectedFaces.Average(f => f.FaceAttributes.Emotion.Happiness),
                    Anger = e.DetectedFaces.Average(f => f.FaceAttributes.Emotion.Anger),
                    Sadness = e.DetectedFaces.Average(f => f.FaceAttributes.Emotion.Sadness),
                    Contempt = e.DetectedFaces.Average(f => f.FaceAttributes.Emotion.Contempt),
                    Disgust = e.DetectedFaces.Average(f => f.FaceAttributes.Emotion.Disgust),
                    Neutral = e.DetectedFaces.Average(f => f.FaceAttributes.Emotion.Neutral),
                    Fear = e.DetectedFaces.Average(f => f.FaceAttributes.Emotion.Fear),
                    Surprise = e.DetectedFaces.Average(f => f.FaceAttributes.Emotion.Surprise)
                };

                this.emotionDataTimelineControl.DrawEmotionData(averageScores);
            }
        }

        private void ShowTimelineFeedbackForNoFaces()
        {
            this.emotionDataTimelineControl.DrawEmotionData(new Emotion { Neutral = 1 });
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            EnterKioskMode();

            if (string.IsNullOrEmpty(SettingsHelper.Instance.FaceApiKey))
            {
                await new MessageDialog("Missing Face API Key. Please enter a key in the Settings page.", "Missing API Key").ShowAsync();
            }
            else
            {
                FaceListManager.FaceListsUserDataFilter = SettingsHelper.Instance.WorkspaceKey + "_RealTime";
                await FaceListManager.Initialize();

                await ResetDemographicsData();
                this.UpdateDemographicsUI();

                await this.cameraControl.StartStreamAsync(isForRealTimeProcessing: true);
                this.StartProcessingLoop();
            }

            base.OnNavigatedTo(e);
        }

        private void UpdateDemographics(ImageAnalyzer img)
        {
            if (this.lastSimilarPersistedFaceSample != null)
            {
                bool demographicsChanged = false;
                // Update the Visitor collection (either add new entry or update existing)
                foreach (var item in this.lastSimilarPersistedFaceSample)
                {
                    Visitor visitor;
                    Guid persistedFaceId = item.SimilarPersistedFace.PersistedFaceId.GetValueOrDefault();
                    if (this.visitors.TryGetValue(persistedFaceId, out visitor))
                    {
                        visitor.Count++;
                        TimeSpan timeAtDisplay = (DateTime.Now - visitor.FirstSeen);
                        MaybeNewOutputMessage($"Visitor id={Math.Abs(visitor.UniqueId.GetHashCode())} has been at display for {timeAtDisplay.Seconds} seconds");
                    }
                    else
                    {
                        demographicsChanged = true;

                        visitor = new Visitor { UniqueId = persistedFaceId, Count = 1, FirstSeen = DateTime.Now };
                        this.visitors.Add(visitor.UniqueId, visitor);
                        this.demographics.Visitors.Add(visitor);

                        string gender = (item.Face.FaceAttributes.Gender == Gender.Male) ? "male" : "female";
                        MaybeNewOutputMessage($"New visitor, id={Math.Abs(visitor.UniqueId.GetHashCode())}, {gender}, {item.Face.FaceAttributes.Age} years", true);


                        // Update the demographics stats. We only do it for new visitors to avoid double counting. 
                        AgeDistribution genderBasedAgeDistribution = null;
                        if (item.Face.FaceAttributes.Gender == Gender.Male)
                        {
                            this.demographics.OverallMaleCount++;
                            genderBasedAgeDistribution = this.demographics.AgeGenderDistribution.MaleDistribution;
                        }
                        else
                        {
                            this.demographics.OverallFemaleCount++;
                            genderBasedAgeDistribution = this.demographics.AgeGenderDistribution.FemaleDistribution;
                        }

                        if (item.Face.FaceAttributes.Age < 16)
                        {
                            genderBasedAgeDistribution.Age0To15++;
                        }
                        else if (item.Face.FaceAttributes.Age < 20)
                        {
                            genderBasedAgeDistribution.Age16To19++;
                        }
                        else if (item.Face.FaceAttributes.Age < 30)
                        {
                            genderBasedAgeDistribution.Age20s++;
                        }
                        else if (item.Face.FaceAttributes.Age < 40)
                        {
                            genderBasedAgeDistribution.Age30s++;
                        }
                        else if (item.Face.FaceAttributes.Age < 50)
                        {
                            genderBasedAgeDistribution.Age40s++;
                        }
                        else
                        {
                            genderBasedAgeDistribution.Age50sAndOlder++;
                        }
                    }
                }

                if (demographicsChanged)
                {
                    this.ageGenderDistributionControl.UpdateData(this.demographics);

                    int totalPeople = demographics.OverallFemaleCount + demographics.OverallMaleCount;
                    NrOfPeopleAtDisplayHistory = totalPeople;
                    if (totalPeople == 3)
                    {
                        SendTeamsMessage.NotifyLotsOfPeopleComing();
                        MaybeNewOutputMessage($"More than 3 people at display last 5 minutes! Sending alert to Teams channel.", true);
                    }
                }

                this.overallStatsControl.UpdateData(this.demographics);
            }
            NrOfPeopleAtDisplay = img.DetectedFaces.Count();
        }

        private void UpdateDemographicsUI()
        {
            this.ageGenderDistributionControl.UpdateData(this.demographics);
            this.overallStatsControl.UpdateData(this.demographics);
        }

        private async Task ResetDemographicsData()
        {
            this.initializingUI.Visibility = Visibility.Visible;
            this.initializingProgressRing.IsActive = true;

            this.demographics = new DemographicsData
            {
                StartTime = DateTime.Now,
                AgeGenderDistribution = new AgeGenderDistribution { FemaleDistribution = new AgeDistribution(), MaleDistribution = new AgeDistribution() },
                Visitors = new List<Visitor>()
            };

            this.visitors.Clear();
            await FaceListManager.ResetFaceLists();

            this.initializingUI.Visibility = Visibility.Collapsed;
            this.initializingProgressRing.IsActive = false;
        }

        public async Task HandleApplicationShutdownAsync()
        {
            await ResetDemographicsData();
        }

        private void EnterKioskMode()
        {
            ApplicationView view = ApplicationView.GetForCurrentView();
            if (!view.IsFullScreenMode)
            {
                view.TryEnterFullScreenMode();
            }
        }

        protected override async void OnNavigatingFrom(NavigatingCancelEventArgs e)
        {
            this.isProcessingLoopInProgress = false;
            Window.Current.Activated -= CurrentWindowActivationStateChanged;
            this.cameraControl.CameraAspectRatioChanged -= CameraControl_CameraAspectRatioChanged;

            await this.ResetDemographicsData();

            await this.cameraControl.StopStreamAsync();
            base.OnNavigatingFrom(e);
        }

        private void OnPageSizeChanged(object sender, SizeChangedEventArgs e)
        {
            this.UpdateCameraHostSize();
        }

        private void UpdateCameraHostSize()
        {
            this.cameraHostGrid.Width = this.cameraHostGrid.ActualHeight * (this.cameraControl.CameraAspectRatio != 0 ? this.cameraControl.CameraAspectRatio : 1.777777777777);
        }

        public DetectedFace GetLastFaceAttributesForFace(BitmapBounds faceBox)
        {
            if (this.lastDetectedFaceSample == null || !this.lastDetectedFaceSample.Any() || !CanReuseCachedResults())
            {
                return null;
            }

            return Util.FindFaceClosestToRegion(this.lastDetectedFaceSample, faceBox);
        }

        public IdentifiedPerson GetLastIdentifiedPersonForFace(BitmapBounds faceBox)
        {
            if (this.lastIdentifiedPersonSample == null || !this.lastIdentifiedPersonSample.Any() || !CanReuseCachedResults())
            {
                return null;
            }

            Tuple<DetectedFace, IdentifiedPerson> match =
                this.lastIdentifiedPersonSample.Where(f => Util.AreFacesPotentiallyTheSame(faceBox, f.Item1.FaceRectangle))
                                               .OrderBy(f => Math.Abs(faceBox.X - f.Item1.FaceRectangle.Left) + Math.Abs(faceBox.Y - f.Item1.FaceRectangle.Top)).FirstOrDefault();
            if (match != null)
            {
                return match.Item2;
            }

            return null;
        }

        public SimilarFace GetLastSimilarPersistedFaceForFace(BitmapBounds faceBox)
        {
            if (this.lastSimilarPersistedFaceSample == null || !this.lastSimilarPersistedFaceSample.Any() || !CanReuseCachedResults())
            {
                return null;
            }

            SimilarFaceMatch match =
                this.lastSimilarPersistedFaceSample.Where(f => Util.AreFacesPotentiallyTheSame(faceBox, f.Face.FaceRectangle))
                                               .OrderBy(f => Math.Abs(faceBox.X - f.Face.FaceRectangle.Left) + Math.Abs(faceBox.Y - f.Face.FaceRectangle.Top)).FirstOrDefault();

            return match?.SimilarPersistedFace;
        }

        private bool CanReuseCachedResults()
        {
            return (DateTime.Now - lastResultsTimestamp).TotalSeconds <= 3;
        }

        private void RadioShowVideo_Checked(object sender, RoutedEventArgs e)
        {
            if (cameraControl != null)
            {
                cameraControl.Visibility = (radioShowVideo.IsChecked ?? false) ? Visibility.Visible : Visibility.Collapsed;
            }
        }
    }

    [XmlType]
    public class Visitor
    {
        [XmlAttribute]
        public Guid UniqueId { get; set; }

        [XmlAttribute]
        public int Count { get; set; }

        public DateTime FirstSeen { get; set; }
    }

    [XmlType]
    public class AgeDistribution
    {
        public int Age0To15 { get; set; }
        public int Age16To19 { get; set; }
        public int Age20s { get; set; }
        public int Age30s { get; set; }
        public int Age40s { get; set; }
        public int Age50sAndOlder { get; set; }
    }

    [XmlType]
    public class AgeGenderDistribution
    {
        public AgeDistribution MaleDistribution { get; set; }
        public AgeDistribution FemaleDistribution { get; set; }
    }

    [XmlType]
    [XmlRoot]
    public class DemographicsData
    {
        public DateTime StartTime { get; set; }

        public AgeGenderDistribution AgeGenderDistribution { get; set; }

        public int OverallMaleCount { get; set; }

        public int OverallFemaleCount { get; set; }

        [XmlArrayItem]
        public List<Visitor> Visitors { get; set; }
    }
}