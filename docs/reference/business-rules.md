# Business rules

The invariants a maintainer could break without noticing: what each rule is, why it exists, where the code enforces
it, and the test that pins it. If a change touches one of these, read the linked page first, and keep (or add) a test
that fails when the rule breaks.

Tests are named as `Project/File.cs` under `tests/` (`Tide.UnitTests` or `Tide.IntegrationTests`) and the method. A
dash means no single test pins the rule; it's held by the code and the tests around it.

- [TLP and editions](#tlp-and-editions)
- [Sends and corrections](#sends-and-corrections)
- [Brief lifecycle](#brief-lifecycle)
- [Metrics locking](#metrics-locking)
- [Actions and SLA](#actions-and-sla)
- [Intake](#intake)
- [Rotation](#rotation)
- [Files](#files)
- [Settings and sign-in](#settings-and-sign-in)
- [Audit](#audit)

## TLP and editions

The detail is in [editions-and-tlp.md](../architecture/editions-and-tlp.md).

| # | Rule | Why | Where enforced | Test |
|---|---|---|---|---|
| T1 | **Every edition is cut by `BriefEditions.Build` or `BuildArticle`.** No rendering filters parts itself. | One function decides what an audience sees; two filters drift and leak. ADR [0005](../decisions/0005-editions-from-one-model.md) | `src/Tide.Application/Briefs/BriefEdition.cs` | `Tide.UnitTests/BriefEditionTests.cs` `Build_MatchesTheGoldenEditions` |
| T2 | **A part counts at its own TLP or its article's, whichever is higher.** | Marking an article AMBER can't be undone by a CLEAR bullet. | `Article.Effective`, `Article.HighestTlp` | `Tide.UnitTests/Audit/BackendAuditTests.cs` `Editions_FilterAndMarkParts_AtTheEffectiveTlp_NeverTheStoredOne` |
| T3 | **An article above the ceiling is left out whole**: title, parts, captions, indicators, actions. | A headline alone can leak a RED victim's name. | `BriefEditions.BuildArticle` | `Tide.UnitTests/BriefEditionTests.cs` `Build_Public_LeavesOutArticlesAboveTheCeiling` |
| T4 | **Indicators, actions and metrics are on the team edition only.** | They're internal working detail, whatever their marking. | `BriefEditions.BuildArticle`, `BriefRenderer` (`MetricsBlock` for `Edition.Team` only) | `Tide.UnitTests/BriefEditionTests.cs` `Build_IndicatorsAndActions_AreOnTheTeamEditionOnly`; `Tide.UnitTests/BriefRendererTests.cs` `Email_Public_NeverShowsMetrics_TeamShowsThemFirst` |
| T5 | **The team note is the team edition's alone**, and every rendering reads it through `BriefEdition.TeamNote`. | It has no marking, so it must never reach the company, the API or SharePoint. | `BriefEdition.ShowTeamNote` | `Tide.UnitTests/TeamNoteRenderTests.cs` `ThePublicEdition_NeverCarriesTheNote`; `Tide.IntegrationTests/TeamNoteTests.cs` `ThePublicApiAndTheSharePointCopy_NeverCarryTheNote` |
| T6 | **The company-wide ceiling never exceeds the team's.** | The public edition must be a subset of the team's. | `DistributionSettings.SaveAsync` (refuses), `ReadAsync` (clamps) | `Tide.IntegrationTests/Audit/AdversarialTests.cs` `TheCompanyWideEdition_NeverCarriesAnArticleAboveTheCeiling` |
| T7 | **An undefined TLP value is never stored**: every marking and ceiling goes through `Tlp.Checked`. | An undefined number compares below every ceiling. | `Tlp.Checked` in every saving service | `Tide.IntegrationTests/ArticleAndBriefTests.cs` `Save_AnUndefinedTlp_IsRefused_NotStoredBelowEveryCeiling`; `DistributionTests.cs` `Ceilings_AnUndefinedTlp_IsRefused` |
| T8 | **A threat's "why it matters" starts at AMBER** (or the article's TLP if higher), and the default only ever raises. | It usually describes our own exposure. | `Article.DefaultWhyItMattersTlp`, `Article.Classify`, `ArticleForm.SetClassification` | `Tide.UnitTests/WhyItMattersDefaultTests.cs` `AThreat_StartsAtAmber_OrTheArticlesTlpWhenHigher` |
| T9 | **Raising an article's TLP pulls its lower parts up; lowering it leaves them.** | A part never counts lower than its article, and a lowered article mustn't silently lower parts. | `ArticleForm.SetTlp` | `Tide.UnitTests/MiscTests.cs` `PartsStoredBelowTheArticle_LoadAtTheArticlesTlp` |
| T10 | **An API token is cut at the lower of its ceiling and the team edition's**, and its actions from an article above it are 404. | A RED token mustn't read more than the team edition sends. | `ApiService.BriefAsync`, `WithheldArticlesAsync` | `Tide.IntegrationTests/ApiTests.cs` `ABrief_IsCutAsTheTeamEditionIs_AtTheLowerCeiling`, `ActionsFromAnArticleAboveTheTokensCeiling_AreWithheld` |
| T11 | **Webhooks never post an action from an article above the team ceiling.** | A webhook receiver is outside the app. | `WebhookService.DeliverDueAsync` | `Tide.IntegrationTests/ApiTests.cs` `Webhooks_LeaveOutActionsAboveTheTeamCeiling_AndMarkEachWithItsArticlesTlp` |
| T12 | **An outreach email is filled only up to its template's ceiling**; the action's own indicators count as AMBER. | It leaves in the analyst's own mail. ADR [0019](../decisions/0019-outreach-emails-are-written-never-sent.md) | `OutreachFill.Cut`, `OutreachFill.UnmarkedTlp` | `Tide.UnitTests/OutreachFillTests.cs` `AnArticleAboveTheCeiling_IsLeftOutWhole_IndicatorsWithIt_AndSaidSo` |
| T13 | **Exports leave out articles above the chosen TLP, whole, and mark each object with its article's TLP.** | Once a file leaves, nothing can be taken back. ADR [0012](../decisions/0012-exports-carry-tlp-and-stable-ids.md) | `ExportService.GatherAsync`, `Stix.Write`, `IndicatorCsv.Write` | `Tide.IntegrationTests/ExportTests.cs` `Brief_InRunningOrder_LeavesOutArticlesAboveTheCeiling` |
| T14 | **Inside the app, the team reads every marking; ceilings govern only what leaves.** Our stack's product page and card are the exception. | Owner decision 2026-11-03: Action owners are trusted with RED. | `ArticleService.RequireRead` (no TLP filter); `StackService.GetAsync`, `CardAsync` | `Tide.IntegrationTests/CommentsAndStackDecisionTests.cs` `AProductsPage_NamesOnlyArticlesAtOrBelowTheTeamCeiling_AndCountsTheRest` |
| T15 | **AI drafting sends nothing above `Ai:MaxTlp`**, and Our stack's notes only when it allows AMBER. | Model providers may only see what the organization approved. ADR [0004](../decisions/0004-opt-in-ai-with-tlp-ceiling.md) | `AssistService.Status`, `PrepareAsync` | `Tide.IntegrationTests/AssistTests.cs` `AboveTheLimit_NothingIsSent_NorCopied` |

## Sends and corrections

Workflow detail is in [distribution.md](../workflows/distribution.md).

| # | Rule | Why | Where enforced | Test |
|---|---|---|---|---|
| S1 | **A send goes only as confirmed**: marking, counts, recipients and a fingerprint of the rendered email must match the plan. | The analyst approved exactly that email to exactly those people. | `DistributionService.SendAsync` against `SendPlan` | `Tide.IntegrationTests/DistributionTests.cs` `Send_IsRefused_WhenTheContentChangedSinceTheConfirmation_ThoughItsCountsAndMarkingDidNot` |
| S2 | **A real send is frozen before the relay is asked** (email HTML, web copy, image copies). | The archive must hold exactly what went, even if articles change later. ADR [0007](../decisions/0007-frozen-sends-and-cid-email.md) | `DistributionService.SendAsync` (`BriefSend`, `BriefSendImage`) | `DistributionTests.cs` `Send_FreezesASnapshot_CopiesImages_LogsIt_AndMarksTheBriefSent` |
| S3 | **Only a send the relay took marks the brief Sent** and publishes it. | A failed or unknown send mustn't lock the day or reach Readers. | `DistributionService.RecordOutcomeAsync`, `SendOutcome` | `DistributionTests.cs` `Send_ARelayFailure_IsLogged_NotPublished_AndTheBriefStaysUnsent`, `Send_ARelayThatThrows_IsRecordedOutcomeUnknown_NotLeftSending` |
| S4 | **One real send of an edition at a time**; the brief's edit version moves in the freezing save. | Two analysts confirming at once must not email the company twice. | `DistributionService.SendAsync` | `Tide.IntegrationTests/Audit/AdversarialTests.cs` `TheSameBrief_SentTwiceAtOnce_GoesOnce`; `DistributionTests.cs` `Send_IsRefused_WhileTheSameEditionIsWithTheRelay` |
| S5 | **A brief is sent from its own day, after its meeting ended** (or ticked "Sent without a meeting"). | The meeting marks articles presented and carries the rest over. | `DistributionService.SendBlocked` | `DistributionTests.cs` `Send_WaitsForTheMeeting_UnlessThereWasNone_AndTheOtherEditionThenFollows`, `Send_IsRefusedBeforeTheBriefsDay_EvenWithoutAMeeting_ButATestCopyGoes` |
| S6 | **With review required, a send waits for the edition's articles to be approved.** | Review is the second pair of eyes before anything leaves. | `ReviewService.RequireApprovedAsync` from `DistributionService` | `Tide.IntegrationTests/ReviewTests.cs` `WithReviewRequired_TheSendWaits_ForTheEditionsArticlesOnly` |
| S7 | **A test send goes to the sender only**, is logged, and changes nothing else. | A test copy must never reach a list or mark the brief. | `DistributionService.SendTestAsync`, `OwnAddress` | `DistributionTests.cs` `SendTest_GoesToTheSenderOnly_IsLogged_AndChangesNothingElse` |
| S8 | **Readers see only frozen company-wide sends**, read from `BriefSend.WebHtml`, never from articles or the search index. | Nothing the company wasn't sent can show or match. | `DistributionService.Published`, `PublishedContent` | `Tide.IntegrationTests/PublishedTests.cs` `TheFilter_FindsASentHeadline_AndNothingFromAnAmberOrUnpublishedArticle` |
| S9 | **Only a send the relay took can be corrected, one correction of a send at a time**, planned and confirmed like a send. | A correction refers to something that actually went. ADR [0015](../decisions/0015-corrections-after-send.md) | `CorrectionService.SendAsync` against `CorrectionPlan` | `Tide.IntegrationTests/CorrectionTests.cs` `OnlyASendTheRelayTook_CanBeCorrected`, `Send_IsRefused_WhileAnotherCorrectionOfTheSendIsWithTheRelay` |
| S10 | **A correction's "now" side is cut at the lower of the send's and the current ceiling**, and repeats no line now above it. | A correction must not leak what the original didn't. | `CorrectionService` (`BriefEditions.Build`) | `CorrectionTests.cs` `AfterTheCeilingIsLowered_ACorrection_RepeatsNoLineThatWentAboveIt_EvenOnceDeletedOrLowered` |
| S11 | **A TLP slip withdraws every company-wide send of the day made before it** from Readers, not from the team. | A slip doesn't say which line slipped, so "put right" can't be told. | `DistributionService.Published` (`Withdrawn`) | `CorrectionTests.cs` `ATlpSlipOfTheDaysLatestSend_AlsoWithdrawsItsEarlierSends_ButNotOneSentAfter` |
| S12 | **`BriefSend` gains no column for corrections**; they're `BriefCorrection` rows. | Every query over sends stays unchanged. ADR 0015 | Schema | `CorrectionTests.cs` `TheMigration_AddsTheCorrectionsTable` |

## Brief lifecycle

Statuses are `Draft`, `Ready`, `Presented`, `Sent` (`BriefStatus`). Detail in
[brief-and-meeting.md](../workflows/brief-and-meeting.md).

| # | Rule | Why | Where enforced | Test |
|---|---|---|---|---|
| B1 | **A brief is Ready only when no article on it fails a blocking check.** | The meeting shouldn't start with a gap. | `BriefService.MarkReadyAsync` (`ArticleChecks.For`) | `Tide.IntegrationTests/ArticleAndBriefTests.cs` `MarkReady_IsRefusedWithAnUnfinishedArticle` |
| B2 | **Back to ready (reopen) is refused while the meeting is live, once sent or finished as not sent, while an edition is with the relay, and once the next brief has moved on.** | Reopening then would undo what the room, the company or the next day already relies on. | `BriefService.ReopenAsync` (`IMeetingsInProgress.IsLive`) | `Tide.IntegrationTests/OwnerDecisionsBriefsTests.cs` `BackToReady_IsRefused_WhileTheMeetingRunsInPresentMode`; `BriefReopenTests.cs` `Reopen_IsRefused_OnceTheBriefIsSent_OrFinishedAsNotSent_AndBeforeTheMeetingEnded` |
| B3 | **Reopening needs both `PresentBriefs` and `WriteArticles`.** | It changes the brief and its articles. | `BriefService.ReopenAsync`, `Capabilities` | `BriefReopenTests.cs` `Reopen_NeedsSomeoneWhoPresents_AndIsInTheAuditTrail_AndIsHeardLive` |
| B4 | **"Finish as not sent" needs a reason, and is refused while a real send is going or its outcome unknown.** | Who decided not to send, and why, must be on record; and an email may still have gone. | `DistributionService.FinishNotSentAsync` | `Tide.IntegrationTests/BriefNotSentTests.cs` `FinishNotSent_RequiresAReason`; `OwnerDecisionsBriefsTests.cs` `NotSent_IsRefused_WhileARealSendIsGoing_OrItsOutcomeIsUnknown` |
| B5 | **Mark distributed only when email is off; either way of finishing makes the brief Sent**, locking its numbers and finishing its articles. | A finished day must look and behave the same however it was finished. | `DistributionService.MarkDistributedAsync`, `FinishNotSentAsync` | `Tide.IntegrationTests/BriefCompletionTests.cs` `MarkDistributed_EmailOff_FinishesTheBrief_LocksItsNumbers_AndRecordsWhoAndWhen` |
| B6 | **The team note can't change once the brief is sent.** | The team email keeps it as sent. | `BriefService.SetTeamNoteAsync` | `Tide.IntegrationTests/TeamNoteTests.cs` `SetTeamNote_IsRefused_OnceTheBriefIsSent_AndTheTeamEmailKeepsItAsSent` |
| B7 | **Articles, briefs and other edited records carry `EditVersion`; a save from a stale copy is refused** with `StaleEditException`. | Two people editing must not silently overwrite each other. | `ArticleService.SaveAsync` and peers | `ArticleAndBriefTests.cs` `Save_FromAStaleCopy_IsRefused` |

## Metrics locking

Detail in [metrics-and-trends.md](../workflows/metrics-and-trends.md).

| # | Rule | Why | Where enforced | Test |
|---|---|---|---|---|
| M1 | **Once a day's brief is Sent, its numbers are fixed for everyone but administrators.** | What went out is the record. | `MetricsService.IsLockedAsync` | `Tide.IntegrationTests/MetricsTests.cs` `OnceTheBriefIsSent_TheDaysNumbersAreFixed_ExceptForAnAdministrator` |
| M2 | **An import never overwrites a value a person typed, or a sent day.** | People win over files. ADR [0014](../decisions/0014-metric-values-from-csv.md) | `MetricImportService` | `Tide.IntegrationTests/MetricImportTests.cs` `AnImport_NeverOverwritesWhatAPersonDecided` |
| M3 | **Reading the same file again changes nothing.** | Imports must be safe to repeat. | `MetricImportService` | `MetricImportTests.cs` `AnImport_SetsValuesWithProvenance_AndReadingItAgainChangesNothing` |
| M4 | **Values only on business days; a weekend's is refused, a holiday's kept if entered.** | Comparisons run between working days. ADR [0018](../decisions/0018-holidays-are-days-off-like-weekends.md) | `MetricsService.SaveValueAsync` | `MetricsTests.cs` `SaveValue_NullClears_AndOnlyBusinessDays` |
| M5 | **A metric's key never changes; renaming or retiring keeps its values.** | Imports and history match by key. | `MetricsService` | `MetricsTests.cs` `RenameAndRetire_KeepHistory_RetiredDropsOffTheDay` |

## Actions and SLA

Detail in [actions-and-hunting.md](../workflows/actions-and-hunting.md).

| # | Rule | Why | Where enforced | Test |
|---|---|---|---|---|
| A1 | **Working an action needs `WriteArticles`, or `WorkActions` and membership of the action's team.** | Teams own actions. ADR [0008](../decisions/0008-teams-own-actions.md) | `ActionService.CanWork` | `Tide.IntegrationTests/ActionTests.cs` `Permissions_TeamMembersWorkTheirTeamsActions_OthersCant`; `Audit/AdversarialTests.cs` `AnActionOwner_CanWorkOnlyTheirOwnTeamsActions` |
| A2 | **Closing Done needs an outcome; a hunt needs what it found; reopening clears it.** Every close goes through `ApplyStatus`. | Reports count outcomes. | `ActionService.ApplyStatus` | `ActionTests.cs` `Status_DoneNeedsAnOutcome_AHuntNeedsWhatItFound_ReopenClears` |
| A3 | **A new action's due date comes from the SLA in business days** (holidays count as weekends). | Due dates must mean the same for every team. | `ActionSla.DueDate`, `SlaSettings` | `Tide.UnitTests/ActionTests.cs` `DueDate_CountsBusinessDays_SkippingTheWeekend`; `Tide.IntegrationTests/ActionTests.cs` `Create_TakesTheSlaDueDate_AndNumbersInOrder` |
| A4 | **An action raised for a past day's brief records that day, but its SLA starts today.** | Reports count it on its day; nobody starts late. | `ActionService.CreateAsync` (`RaisedOn`) | `ActionTests.cs` `Create_ForAPastDaysBrief_RecordsThatDay_ButTheSlaStartsToday` |
| A5 | **An action raised in the meeting takes its article's priority, capped at P2 for Awareness, Outreach and Other.** | A notice shouldn't page someone as P1. | `ActionService.QuickPriority` | `ActionTests.cs` `QuickAdd_TakesTheArticlesPriority_CappedForANotice_AndTheDueDateStillFollowsTheSla` |
| A6 | **An Action owner can't relink their action to another article.** | The article sets the action's TLP and context. | `ActionService` | `Audit/AdversarialTests.cs` `AnActionOwner_CannotRelinkTheirActionToAnotherArticle` |
| A7 | **TIDE never sends an outreach email**; marking one sent is refused while a `[[…]]` marker is left. | It's written for the analyst to send from their own mail. ADR 0019 | `OutreachService.MarkSentAsync` | `Tide.IntegrationTests/OutreachTests.cs` `MarkingItSent_IsRefused_WithoutAValidAddress_OrWithAMarkerLeft` |
| A8 | **A reminder email goes once**: each is marked (`NotificationMark`) as it goes. | A scan stopped partway must not email twice. | `ReminderService` | `Tide.UnitTests/ActionTests.cs` `DueSoon_AScanStoppedPartway_KeepsTheMarksOfWhatWent_AndDoesntEmailThoseAgain` |
| A9 | **The API changes an action's status and note only**, never deletes or reassigns. | A playbook's token must not do what a person would be asked about. ADR [0016](../decisions/0016-api-tokens-scopes-and-signed-webhooks.md) | `ApiService.UpdateActionAsync` | `Tide.IntegrationTests/ApiTests.cs` `AToken_ReadsAndChangesActions_AsItsScopesAllow` |
| A10 | **The starter saved queries, outreach templates and playbooks are each added once, and only to an empty library**, saved by "TIDE"; the `Data:StarterSavedQueries`, `Data:StarterOutreachTemplates` and `Data:StarterPlaybooks` settings mark each set as offered, and a library that already has entries is only marked. | Starters the team deletes or retires must not come back, and a team's own library isn't padded. | `DatabaseInitializer.SeedStarterLibrariesAsync` | `Tide.IntegrationTests/StarterLibrariesSeedTests.cs` `ANewInstall_GetsEachStarterSet_SavedByTide_AndAnotherStartAddsNothing`, `ALibraryTheTeamEmptied_StaysEmpty`, `ALibraryTheTeamAlreadyKeeps_GetsNoStarters_ButIsMarkedDone` |

## Intake

Detail in [intake-and-triage.md](../workflows/intake-and-triage.md).

| # | Rule | Why | Where enforced | Test |
|---|---|---|---|---|
| I1 | **Intake polls nothing until an administrator turns it on.** | TIDE reaches out only when asked. ADR [0009](../decisions/0009-intake-reaches-out-only-when-asked.md) | `IntakeHostedService` (`Intake:Enabled`) | `Tide.IntegrationTests/IntakeTests.cs` `PollDue_OnlyWhenIntakeIsOn_AndTheSourceIsDue` |
| I2 | **One item per canonical address.** | A story syndicated many times is triaged once. | `UrlCanonicalizer`, `IntakeService` | `IntakeTests.cs` `Poll_StoresNewItemsOnce_ByCanonicalAddress_AndFlagsARewordedRepeat` |
| I3 | **Promoting claims the item in one statement first**; a group's reports are claimed all or none. | Two analysts promoting at once make one article. | `IntakeService.PromoteManyAsync` | `IntakeTests.cs` `Promote_ByTwoAnalystsAtOnce_MakesOneArticle`, `PromoteAsOne_ClaimsNoneOfThem_WhenOneIsAlreadyTaken` |
| I4 | **One poll of a source at a time; Pull now is refused within `Intake:PullNowMinutes` (15) of its last poll**, administrators included. | No duplicate items, and no hammering a publisher. | `SourcePollGuard`, `IntakeService.PullNowAsync` | `Tide.IntegrationTests/SourcePullTests.cs` `PullNow_TwoAtOnce_MakeOnePull`, `PullNow_InsideTheWindow_IsRefusedSayingWhen_AndAfterItIsAllowed` |
| I5 | **Every outbound fetch goes through `IOutboundHttp` and `AddressPolicy`.** | A feed or its redirect mustn't reach the server's own network. | `OutboundHttp`, `AddressPolicy` | `Tide.IntegrationTests/OutboundHttpTests.cs` `Get_ARedirectToARefusedAddress_IsNotFollowed` |
| I6 | **An item proposed through the API lands in New**; an analyst still decides it. | Automation proposes, people decide. | `ApiService.ProposeAsync` | `ApiTests.cs` `AProposedItem_LandsInNew_UnderASourceNamedForTheCaller` |
| I7 | **A promoted item starts at its source's TLP.** | The publisher's marking travels with its content. | `IntakeService.PromoteManyAsync` (`Source.DefaultTlp`) | `IntakeTests.cs` `Promote_MakesADraftArticle_WithTheSourcesTlpAndTheItemsCves_Once` |
| I8 | **The inbox is paged, never cut**: 50 items a page with the count of every match, whatever the filters. | An item past a silent cap would never be triaged. | `IntakeService.InboxPageAsync` (`InboxFilter.PageSize`) | `Tide.IntegrationTests/PagingAndFiltersTests.cs` `TheInbox_PagesInTheDatabase_SayingHowManyInAll_NeverCutSilently`, `NewAndOlder_PageToo_TheirCountsStayTotals` |

## Rotation

Detail in [rotation.md](../workflows/rotation.md).

| # | Rule | Why | Where enforced | Test |
|---|---|---|---|---|
| R1 | **Only the colleague taking on the days approves a cover or swap**, even one an administrator arranged. | Nobody is volunteered. ADR [0010](../decisions/0010-rotation-changes-approved-by-the-colleague.md) | `RotationService.ApproveAsync` | `Tide.IntegrationTests/RotationTests.cs` `Cover_IsApprovedByThePartnerOnly_ThenTheDaysMove_AndTheManagerIsTold`, `AnAdministrator_ArrangesCover_StillApprovedByTheColleague_AndCanUndoIt` |
| R2 | **The requester must hold every day they give (and, for a swap, the colleague every day back), when asked and again when approved.** | Days change hands meanwhile. | `RotationService.RequestAsync`, `ApproveAsync` | `RotationTests.cs` `Request_MustBeForDaysYouHave_InTheFuture_AndWithinTheLimit`, `Approval_IsRefused_WhenTheDaysChangedHandsMeanwhile` |
| R3 | **Of two approvals for the same day at once, only one goes through.** | A day has one presenter. | `RotationService.ApproveAsync` | `RotationTests.cs` `Approval_OfTwoRequestsForTheSameDay_AtTheSameMoment_LetsOnlyOneThrough` |
| R4 | **Approved late, only the days still to come change hands**; with every day past it's refused. | The past is recorded, not rewritten. | `RotationService.ApproveAsync` | `RotationTests.cs` `ApprovedLate_OnlyTheDaysStillToCome_ChangeHands`, `ApprovedLate_WithEveryDayPast_IsRefused_AndCanBeDeclined` |
| R5 | **Only an administrator undoes an approved change**, and only its days still to come go back. | An approval is a commitment between two people. | `RotationService` | `RotationTests.cs` `Undo_OfAChangePartlyPast_GivesBackOnlyTheDaysToCome` |
| R6 | **A past day's cover is recorded, not requested**, by the people involved or an administrator. | The brief itself is the evidence. | `RotationService` | `RotationTests.cs` `RecordingAPastCover_IsForThePeopleInvolved_OrAnAdministrator` |

## Files

Detail in [security.md](../architecture/security.md#files).

| # | Rule | Why | Where enforced | Test |
|---|---|---|---|---|
| F1 | **A screenshot is accepted and served only if its bytes are PNG, JPEG, GIF or WebP**, served with the sniffed type. | Stored content can never ride the inline endpoint as script. | `ImageSniffer`, `ScreenshotService` | `Tide.UnitTests/Audit/SecurityAuditTests.cs` `TheSniffer_RefusesAnythingButARasterImage` |
| F2 | **A Reader gets only images frozen in a sent company-wide edition**; a Visitor only their seated brief's. | An unpublished image can't be fetched by guessing its id. | `ScreenshotService.OpenAsync` | `Tide.IntegrationTests/Audit/SecurityAuditTests.cs` `AReader_SeesNoScreenshotThatWasNeverPublished_ByAnyRoute` |
| F3 | **Every file download is an `octet-stream` attachment, `nosniff`, `no-store`, sandboxed.** | Whatever the browser makes of a sample can't run in TIDE's origin. | `FileDownloads.Plain`, `Zipped` | `Tide.IntegrationTests/ActionFilesTests.cs` `ThePlainFile_IsAnAttachment_NeverSniffed_AndSandboxed` |
| F4 | **A risky file downloads inside a zip with the password `infected` by default.** | A double-click can't run it, and antivirus doesn't eat it. | `FileRisk.ZipFirst`, `Files:ZipDownloads` | `ActionFilesTests.cs` `ARiskyFile_DownloadsInAZip_ThatOpensWithThePasswordInfected` |
| F5 | **An image is shown inline only when its bytes, name and declared type agree.** | A mislabeled file is a download, never a picture. | `FileRisk.Inspect` | `ActionFilesTests.cs` `AnImageWhoseBytesDontMatch_IsNeverShownInline` |
| F6 | **Removing a file needs a reason and keeps its bytes, hash and row; only an administrator restores.** Deleting a comment keeps its files as removed. | Evidence must survive a mistake. | `ActionFileService.RemoveAsync`, `RestoreAsync` | `ActionFilesTests.cs` `Removing_NeedsAReason_KeepsTheBytes_AndOnlyAnAdministratorRestores` |
| F7 | **Every file's SHA-256 is recorded on arrival** and named in the history. | Evidence is identified by its hash. | `ActionFileService` (`StoredFile.Sha256`) | `ActionFilesTests.cs` `AFile_IsStoredWithItsHashAndSize_AndTheHistoryAndAuditTrailNameTheHash` |

## Settings and sign-in

Detail in [configuration.md](../operations/configuration.md) and [security.md](../architecture/security.md).

| # | Rule | Why | Where enforced | Test |
|---|---|---|---|---|
| C1 | **Production requires `Auth:Mode=Windows`.** | The dev handler signs everyone in with any roles asked for. | `AuthModeGuard.UseWindows` | `Tide.IntegrationTests/Audit/SecurityAuditTests.cs` `DevSignIn_NeverStartsWhereItIsNotAllowed`, `Production_RefusesTheDemoOptIn` |
| C2 | **Authorization runs on permission claims, never role names; every service checks again.** | A page that forgets its policy still can't write. ADR [0002](../decisions/0002-permissions-as-code.md) | `Policies.Required`, `ICurrentUser.Has` in each service | `Tide.IntegrationTests/CapabilitiesTests.cs` `EachRow_MatchesTheServiceCheck_ForEachRole` |
| C3 | **`Permission` and other stored enums are append-only.** | They're stored by number in claims, rows and audit JSON. | `src/Tide.Domain/Enums` | - |
| C3a | **A role is never renamed.** | Role names are stored as text in `RoleMappings.RoleName` and `AppUser.RolesCsv`; a renamed role would silently grant nothing. | `RoleDefinitions.TryParseRole` (unknown names ignored) | - |
| C4 | **A group not named in time grants nothing by name**, and that request's roles aren't stored. | Fail closed when AD is slow. | `RoleClaimsTransformer`, `UserMirrorMiddleware` | `Audit/SecurityAuditTests.cs` `AGroupNotNamedInTime_GrantsNothingByName_AndTheRequestSaysSo` |
| C5 | **The last mapping granting Admin can't be removed.** | TIDE can't lock itself out. | `RoleMappingService.RemoveAsync` (serializable) | `Tide.IntegrationTests/BugRound2PlatformTests.cs` `TwoAdminsRemovingTheLastTwoAdminMappingsAtOnce_LeaveOne` |
| C6 | **Security-sensitive and infrastructure settings stay on the server**: only `SettingsCatalog.InApp` keys are saved in the app or imported. | A database writer must not redirect email or weaken sign-in. | `SettingsCatalog`, `SavedSettings.RefreshAsync`, `ConfigBundleService` | `Tide.IntegrationTests/OperationalSettingsTests.cs` `TheCatalog_ListsEachOperationalSettingInTheApp_AndNotOnTheServer` |
| C7 | **A saved setting is checked on save, on import, and again when read**; one that no longer passes is ignored. | A bad row must not break the app; the server's value holds. | `OperationalSettings.Problem`, `SavedSettings.RefreshAsync` | `OperationalSettingsTests.cs` `ARowThatNoLongerPassesItsCheck_IsIgnored_SoTheServersValueHolds` |
| C8 | **API tokens are stored only as a SHA-256 hash; webhook secrets only encrypted.** | A database read must not yield a working credential. | `ApiTokenService.Hash`, `DataProtectionSecrets` | `Tide.IntegrationTests/ApiTests.cs` `AToken_IsStoredOnlyAsAHash_AndStopsWhenRevokedOrExpired` |

## Audit

Detail in [security.md](../architecture/security.md#the-audit-trail-and-secrets).

| # | Rule | Why | Where enforced | Test |
|---|---|---|---|---|
| U1 | **Every change to an audited entity is recorded in the same save.** | A change and its record commit or fail together. | `AuditInterceptor` | `Tide.IntegrationTests/AuditLogTests.cs` `TheLog_ListsEveryKindOfRecord_NewestFirst_NamedAndLinked` |
| U2 | **Set-based writes (`ExecuteUpdate`, `ExecuteDelete`) bypass the interceptor and the stamps, so they're used only for bookkeeping**: poll results, feed-item triage status and claims, the feed prune, token call counts, webhook delivery state, the screenshot-change stamp. | Anything else would be an unaudited change. | Code review | `Tide.IntegrationTests/ApiTests.cs` `ARun_WritesNoAuditRow_AndTheSecretIsntInTheTrail` |
| U3 | **A secret is never written to the trail**: secret settings and webhook secrets as "set", source addresses without their key. | Admins read the log; it mustn't hold credentials. | `AuditInterceptor.MaskFor`, `SettingsCatalog.IsSecret`, `SecretMask` | `Tide.IntegrationTests/Audit/SecurityAuditTests.cs` `TheAuditTrail_NeverHoldsASettingNamedLikeASecret`, `TheAuditTrail_KeepsAFeedAddressWithoutItsKey` |
| U4 | **Display masks again, and the words filter never matches a secret's value.** | Defends against rows written before masking, and against probing. | `AuditLogService`, `HistoryService` | `AuditLogTests.cs` `ASecret_ShowsOnlyAsSetOrChanged_OnThePageAndInTheCsv_AndIsntSearchable` |
| U5 | **History is analysts only; the audit log administrators only.** | The log holds every field at every marking. | `HistoryService`, `AuditLogService.RequireAdministrator` | `Tide.IntegrationTests/HistoryTests.cs` `OnlyAnalysts_SeeTheHistory`; `AuditLogTests.cs` `OnlyAdministrators_ReadTheLog` |
| U6 | **Machine bookkeeping isn't audited row by row** (feed items, CVE records, detection rules, kept article text, preferences, notification marks). | It would drown people's changes. | `AuditInterceptor.NotAudited` | `AuditLogTests.cs` `PushedRules_AndKeptArticleText_AreNotAuditedRowByRow` |
| U7 | **CSV cells never start a formula.** | Exports open in Excel. | `Csv.Cell` | `Tide.UnitTests/Audit/SecurityAuditTests.cs` `ACsvCell_NeverStartsAFormula` |
