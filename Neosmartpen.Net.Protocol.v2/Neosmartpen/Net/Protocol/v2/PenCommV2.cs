﻿using Neosmartpen.Net.Filter;
using Neosmartpen.Net.Metadata;
using Neosmartpen.Net.Metadata.Model;
using Neosmartpen.Net.Support;
using Neosmartpen.Net.Support.Encryption;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Text;
using System.Threading;

namespace Neosmartpen.Net.Protocol.v2
{
    /// <summary>
    /// Provides fuctions that can handle F50, F120, F51 Smartpen
    /// </summary>
    public class PenCommV2 : PenComm
    {
        private PenCommV2Callbacks Callback;

        /// <summary>
        /// Gets a name of a device.
        /// </summary>
        public string DeviceName { get; private set; }

        /// <summary>
        /// Gets a version of a firmware.
        /// </summary>
        public string FirmwareVersion { get; private set; }

        /// <summary>
        /// Gets a version of a protocol.
        /// </summary>
        public string ProtocolVersion { get; private set; }

        /// <summary>
        /// Gets a product name of the device.
        /// </summary>
        public string SubName { get; private set; }

        /// <summary>
        /// Gets the device identifier.
        /// </summary>
        public string MacAddress { get; private set; }

        public short DeviceType { get; private set; }
        
        /// <summary>
        /// Gets the maximum level of force sensor.
        /// </summary>
        public short MaxForce { get; private set; }

		private const string SupportedProtocolVersion = "2.12";

		private long mTime = -1L;

        private PenTipType mPenTipType = PenTipType.Normal;

        private int mPenTipColor = -1;

        public enum PenTipType { Normal = 0, Eraser = 1 };

        private bool IsStartWithDown = false;

        private int mDotCount = -1;

        private int mCurSection = -1, mCurOwner = -1, mCurNote = -1, mCurPage = -1;

        //private Packet mPrevPacket = null;

        private int mTotalOfflineStroke = -1, mReceivedOfflineStroke = 0, mTotalOfflineDataSize = -1;

        private int mPrevIndex = -1;
        private byte mPrevCount = 0;
        private Dot mPrevDot = null;

		private readonly string DEFAULT_PASSWORD = "0000";
        private static readonly float PEN_PROFILE_SUPPORT_PROTOCOL_VERSION = 2.10f;
		private readonly string F121 = "NWP-F121";
		private readonly string F121MG = "NWP-F121MG";

		private bool reCheckPassword = false;
		private string newPassword;

		private FilterForPaper dotFilterForPaper = null;
		private FilterForPaper offlineFillterForPaper = null;

        private static readonly float PEN_ENCRYPTION_SUPPORT_PROTOCOL_VERSION = 2.14f;

        /// <summary>
        /// Whether to support secure communication mode
        /// </summary>
        public bool IsSupportEncryption
        {
            get
            {
                try
                {
                    float supportVersion = PEN_ENCRYPTION_SUPPORT_PROTOCOL_VERSION;
                    float receiveVersion = float.Parse(ProtocolVersion);
                    return receiveVersion == supportVersion;
                }
                catch
                {
                    return false;
                }
            }
        }

        private PrivateKey rsaKeys = null;
        private AES256Cipher aes256Cipher = null;

        private bool isPenAuthenticated = false;
        private bool isEncryptedMode = false;
        private bool doGetAesKey = false;

        /// <summary>
        /// Cause when secure communication fails
        /// </summary>
        public enum SecureCommunicationFailureReason
        {
            /// <summary>
            /// Certificate Expiration
            /// </summary>
            CertificateExpired,
            /// <summary>
            /// Private key not set
            /// </summary>
            NoPrivateKey,
            /// <summary>
            /// Private key is invalid
            /// </summary>
            InvalidPrivateKey,
            /// <summary>
            /// The cause is unknown
            /// </summary>
            UnknownError
        };

        /// <summary>
        /// Processing Results for Certificate Update Requests
        /// </summary>
        public enum CertificateUpdateResult
        {
            /// <summary>
            /// Update successful
            /// </summary>
            Success,
            /// <summary>
            /// Copy of file failed
            /// </summary>
            FileCopyFailed,
            /// <summary>
            /// File replacement failed
            /// </summary>
            FileReplacementFailed,
            /// <summary>
            /// Expiration date error
            /// </summary>
            InvalidExpirationDate,
            /// <summary>
            /// Use old protocol
            /// </summary>
            InvalidProtocolVersion,
            /// <summary>
            /// Internal processing error
            /// </summary>
            InternalProcessingError,
            /// <summary>
            /// The cause is unknown
            /// </summary>
            UnknownError
        };

        /// <summary>
        /// Results of Processing Deletion Requests for Installed Certificates
        /// </summary>
        public enum CertificateDeleteResult
        {
            /// <summary>
            /// Certificate Deletion Successful
            /// </summary>
            Success,
            /// <summary>
            /// No certificate installed
            /// </summary>
            NoCertificate,
            /// <summary>
            /// The serial number entered does not match the installed certificate
            /// </summary>
            InvalidSerialCode,
            /// <summary>
            /// File deletion failed
            /// </summary>
            FileDeleteFailed,
            /// <summary>
            /// Use old protocol
            /// </summary>
            InvalidProtocolVersion,
            /// <summary>
            /// The cause is unknown
            /// </summary>
            UnknownError
        };

        public enum UsbMode : byte { Disk = 0, Bulk = 1 };

        public enum DataTransmissionType : byte { Event = 0, RequestResponse = 1 };
		
        public bool HoverMode
        {
            get;
            private set;
        }

        /// <inheritdoc/>
        public override string Version
        {
            get { return "2.00"; }
        }

        /// <inheritdoc/>
        public override uint DeviceClass
        {
            get { return 0x2510; }
        }

        /// <inheritdoc/>
        public override string Name
        {
            get;
            set;
        }

        public PenCommV2( PenCommV2Callbacks handler, IProtocolParser parser = null, IMetadataManager metadataManager = null ) : base( parser == null ? new ProtocolParserV2() : parser, metadataManager )
        {
            Callback = handler;
			dotFilterForPaper = new FilterForPaper(SendDotReceiveEvent);
			offlineFillterForPaper = new FilterForPaper(AddOfflineFilteredDot);
            Parser.PacketCreated += mParser_PacketCreated;
        }

        private void mParser_PacketCreated( object sender, PacketEventArgs e )
        {
            ParsePacket( e.Packet as Packet );
        }

        protected override void OnConnected()
        {
            Thread.Sleep( 500 );
            mPrevIndex = -1;
            ReqVersion();
        }

        protected override void OnDisconnected()
        {
			if (IsStartWithDown && IsBeforeMiddle && mPrevDot != null)
			{
				MakeUpDot();

				mTime = -1;
				SessionTs = -1;

				IsStartWithDown = false;
				IsBeforeMiddle = false;
				IsStartWithPaperInfo = false;

				mDotCount = 0;

				mPrevDot = null;
			}

            aes256Cipher = null;

            isPenAuthenticated = false;
            isEncryptedMode = false;
            doGetAesKey = false;

            Callback.onDisconnected( this );
        }

		int offlineDataPacketRetryCount = 0;
		private void ParsePacket( Packet pk )
        {
            Cmd cmd = (Cmd)pk.Cmd;

            //System.Console.Write( "Cmd : {0}", cmd.ToString() );

            switch ( cmd )
            {
                case Cmd.VERSION_RESPONSE:
                    {
                        DeviceName = pk.GetString( 16 );
                        FirmwareVersion = pk.GetString( 16 );
                        ProtocolVersion = pk.GetString( 8 );
                        SubName = pk.GetString( 16 );
                        DeviceType = pk.GetShort();
                        MaxForce = -1;
                        MacAddress = BitConverter.ToString( pk.GetBytes( 6 ) ).Replace( "-", "" );
						bool isMG = isF121MG(MacAddress);
						if (isMG && DeviceName.Equals(F121) && SubName.Equals("Mbest_smartpenS"))
							DeviceName = F121MG;

						IsUploading = false;

						EventCount = 0;

						ReqPenStatus();
                    }
                    break;

                #region request pen data
                case Cmd.ONLINE_PEN_DATA_REQUEST:
                    {
                        ParseOnlineDataRequest(pk);
                    }
                    break;
                #endregion

                #region event
                case Cmd.SHUTDOWN_EVENT:
                    {
                        byte reason = pk.GetByte();

                        System.Console.Write( " => SHUTDOWN_EVENT : {0}", reason );

                        if (reason == 2)
                        {
                            // 업데이트를 위해 파워가 꺼지면 업데이트 완료 콜백
                            Callback.onReceiveFirmwareUpdateResult(this, true);
                        }
                    }
                    break;

                case Cmd.LOW_BATTERY_EVENT:
                    {
                        int battery = (int)( pk.GetByte() & 0xff );
                        Callback.onReceiveBatteryAlarm( this, battery );
                    }
                    break;

                case Cmd.ONLINE_PEN_UPDOWN_EVENT:
                case Cmd.ONLINE_PEN_DOT_EVENT:
                case Cmd.ONLINE_PAPER_INFO_EVENT:
				case Cmd.ONLINE_PEN_ERROR_EVENT:
				case Cmd.ONLINE_NEW_PEN_DOWN_EVENT:
				case Cmd.ONLINE_NEW_PEN_UP_EVENT:
				case Cmd.ONLINE_NEW_PEN_DOT_EVENT:
				case Cmd.ONLINE_NEW_PAPER_INFO_EVENT:
				case Cmd.ONLINE_NEW_PEN_ERROR_EVENT:
					{
                        ParseDotPacket( cmd, pk );
                    }
                    break;
                #endregion

                #region setting response
                case Cmd.SETTING_INFO_RESPONSE:
					{
						// 비밀번호 사용 여부
						bool lockyn = pk.GetByteToInt() == 1;

						// 비밀번호 입력 최대 시도 횟수
						int pwdMaxRetryCount = pk.GetByteToInt();

						// 비밀번호 입력 시도 횟수
						int pwdRetryCount = pk.GetByteToInt();

						// 1970년 1월 1일부터 millisecond tick
						long time = pk.GetLong();

						// 사용하지 않을때 자동으로 전원이 종료되는 시간 (단위:분)
						short autoPowerOffTime = pk.GetShort();

						// 최대 필압
						short maxForce = pk.GetShort();

						// 현재 메모리 사용량
						int usedStorage = pk.GetByteToInt();

						// 펜의 뚜껑을 닫아서 펜의 전원을 차단하는 기능 사용 여부
						bool penCapOff = pk.GetByteToInt() == 1;

						// 전원이 꺼진 펜에 필기를 시작하면 자동으로 펜의 켜지는 옵션 사용 여부
						bool autoPowerON = pk.GetByteToInt() == 1;

						// 사운드 사용여부
						bool beep = pk.GetByteToInt() == 1;

						// 호버기능 사용여부
						bool hover = pk.GetByteToInt() == 1;
						HoverMode = hover;

						// 남은 배터리 수치
						int batteryLeft = pk.GetByteToInt();

						// 오프라인 데이터 저장 기능 사용 여부
						bool useOffline = pk.GetByteToInt() == 1;

                        // 필압 단계 설정 (0~4) 0이 가장 민감
                        //short fsrStep = (short)pk.GetByteToInt();
                        pk.Move(1);
                        short fsrStep = 0;

                        UsbMode usbmode = pk.GetByteToInt() == 0 ? UsbMode.Disk : UsbMode.Bulk;

						bool downsampling = pk.GetByteToInt() == 1;

						string btLocalName = pk.GetString(16).Trim();

                        pk.Move(1);
                        DataTransmissionType dataTransmissionType = DataTransmissionType.Event;

                        int encryptionType = 0;

                        if (IsSupportEncryption)
                        {
                            encryptionType = pk.GetByteToInt();
                            isEncryptedMode = encryptionType == 0 ? false : true;
                        }

                        // 최초 연결시
                        if (!isPenAuthenticated)
						{
							MaxForce = maxForce;

							Callback.onConnected(this, MacAddress, DeviceName, FirmwareVersion, ProtocolVersion, SubName, MaxForce);

							if (lockyn)
							{
								Callback.onPenPasswordRequest(this, pwdRetryCount, pwdMaxRetryCount);
							}
							else
							{
                                if (encryptionType == 0 || doGetAesKey)
                                {
                                    isPenAuthenticated = true;
                                    ReqSetupTime(Time.GetUtcTimeStamp());
                                    Callback.onPenAuthenticated(this);
                                }
                                else if (rsaKeys == null)
                                {
                                    Callback.onPrivateKeyRequest(this);
                                }
                                else
                                {
                                    if (encryptionType == 1)
                                    {
                                        ReqEncryptionKey();
                                    }
                                    else if (encryptionType == 2)
                                    {
                                        Callback.onSecureCommunicationFailureOccurred(this, SecureCommunicationFailureReason.CertificateExpired);
                                    }
                                    else
                                    {
                                        Callback.onSecureCommunicationFailureOccurred(this, SecureCommunicationFailureReason.UnknownError);
                                        base.Clean();
                                    }
                                }
							}
						}
						else
						{
							Callback.onReceivePenStatus(this, lockyn, pwdMaxRetryCount, pwdRetryCount, time, autoPowerOffTime, MaxForce, batteryLeft, usedStorage, useOffline, autoPowerON, penCapOff, hover, beep, fsrStep, usbmode, downsampling, btLocalName, dataTransmissionType);
						}
					}
					break;

                case Cmd.SETTING_CHANGE_RESPONSE:
                    {
                        int inttype = pk.GetByteToInt();

                        SettingType stype = (SettingType)inttype;

                        bool result = pk.Result == 0x00;

                        switch ( stype )
                        {
                            case SettingType.Timestamp:
                                Callback.onPenTimestampSetUpResponse(this, result);
                                break;

                            case SettingType.AutoPowerOffTime:
                                Callback.onPenAutoShutdownTimeSetUpResponse( this, result );
                                break;

                            case SettingType.AutoPowerOn:
                                Callback.onPenAutoPowerOnSetUpResponse( this, result );
                                break;

                            case SettingType.Beep:
                                Callback.onPenBeepSetUpResponse( this, result );
                                break;

                            case SettingType.Hover:
                                Callback.onPenHoverSetUpResponse( this, result );
                                break;

                            case SettingType.LedColor:
                                Callback.onPenColorSetUpResponse( this, result );
                                break;

                            case SettingType.OfflineData:
                                Callback.onPenOfflineDataSetUpResponse( this, result );
                                break;

                            case SettingType.PenCapOff:
                                Callback.onPenCapPowerOnOffSetupResponse( this, result );
                                break;

                            case SettingType.Sensitivity:
                                Callback.onPenSensitivitySetUpResponse( this, result );
                                break;

                            case SettingType.UsbMode:
                                Callback.onPenUsbModeSetUpResponse( this, result );
                                break;

                            case SettingType.DownSampling:
                                Callback.onPenDownSamplingSetUpResponse( this, result );
                                break;

                            case SettingType.BtLocalName:
                                Callback.onPenBtLocalNameSetUpResponse( this, result );
                                break;

                            case SettingType.FscSensitivity:
                                Callback.onPenFscSensitivitySetUpResponse( this, result );
                                break;

                            case SettingType.DataTransmissionType:
                                Callback.onPenDataTransmissionTypeSetUpResponse( this, result );
                                break;

                            case SettingType.BeepAndLight:
                                Callback.onPenBeepAndLightResponse( this, result );
                                break;
                        }
                    }
                    break;
                #endregion

                #region password response
                case Cmd.PASSWORD_RESPONSE:
                    {
                        int status = pk.GetByteToInt();
                        int cntRetry = pk.GetByteToInt();
                        int cntMax = pk.GetByteToInt();

                        if ( status == 1 )
                        {
							if (reCheckPassword)
							{
								Callback.onPenPasswordSetUpResponse(this, true);
								reCheckPassword = false;
								break;
							}

							ReqSetupTime(Time.GetUtcTimeStamp());
							Callback.onPenAuthenticated( this );
						}
						else
                        {
							if (reCheckPassword)
							{
								reCheckPassword = false;
								Callback.onPenPasswordSetUpResponse(this, false);
							}
							else
							{
								Callback.onPenPasswordRequest(this, cntRetry, cntMax);
							}
                        }
                    }
                    break;

                case Cmd.PASSWORD_CHANGE_RESPONSE:
                    {
                        int cntRetry = pk.GetByteToInt();
                        int cntMax = pk.GetByteToInt();
						
						if (pk.Result == 0x00)
						{
							reCheckPassword = true;
							ReqInputPassword(newPassword);
						}
						else
						{
							newPassword = string.Empty;
							Callback.onPenPasswordSetUpResponse(this, false);
						}
					}
                    break;
                #endregion

                #region offline response
                case Cmd.OFFLINE_NOTE_LIST_RESPONSE:
                    {
                        short length = pk.GetShort();

                        List<OfflineDataInfo> result = new List<OfflineDataInfo>();

                        for ( int i = 0; i < length; i++ )
                        {
                            byte[] rb = pk.GetBytes( 4 );

                            int section = (int)( rb[3] & 0xFF );
                            int owner = ByteConverter.ByteToInt( new byte[] { rb[0], rb[1], rb[2], (byte)0x00 } );
                            int note = pk.GetInt();

                            result.Add( new OfflineDataInfo( section, owner, note ) );
                        }

                        Callback.onReceiveOfflineDataList( this, result.ToArray() );
                    }
                    break;

                case Cmd.OFFLINE_PAGE_LIST_RESPONSE:
                    {
                        byte[] rb = pk.GetBytes( 4 );

                        int section = (int)( rb[3] & 0xFF );
                        int owner = ByteConverter.ByteToInt( new byte[] { rb[0], rb[1], rb[2], (byte)0x00 } );
                        int note = pk.GetInt();

                        short length = pk.GetShort();

                        int[] pages = new int[length];

                        for ( int i = 0; i < length; i++ )
                        {
                            pages[i] = pk.GetInt();
                        }

                        Callback.onReceiveOfflineDataPageList( this, section, owner, note, pages);
                    }
                    break;

                case Cmd.OFFLINE_DATA_RESPONSE:
                    {
                        bool result = pk.Result == 0x00;

                        mTotalOfflineStroke = pk.GetInt();
                        mReceivedOfflineStroke = 0;
                        mTotalOfflineDataSize = pk.GetInt();

                        bool isCompressed = pk.GetByte() == 1;

                        if (mTotalOfflineStroke == 0)
                        {
                            Callback.onFinishedOfflineDownload(this, false);
                        }
                        else
                        {
                            Callback.onStartOfflineDownload(this);
                        }
                    }
                    break;

                case Cmd.OFFLINE_PACKET_REQUEST:
                    {
                        #region offline data parsing

                        List<Stroke> result = new List<Stroke>();

                        short packetId = pk.GetShort();
                        
                        bool isCompressed = pk.GetByte() == 1;

                        short sizeBefore = pk.GetShort();
                        
                        short sizeAfter = pk.GetShort();

                        short location = (short)( pk.GetByte() & 0xFF );

                        byte[] rb = pk.GetBytes( 4 );

                        int section = (int)( rb[3] & 0xFF );
                        
                        int owner = ByteConverter.ByteToInt( new byte[] { rb[0], rb[1], rb[2], (byte)0x00 } );
                        
                        int note = pk.GetInt();

                        short strCount = pk.GetShort();

                        mReceivedOfflineStroke += strCount;

                        System.Console.WriteLine( " packetId : {0}, isCompressed : {1}, sizeBefore : {2}, sizeAfter : {3}, size : {4}", packetId, isCompressed, sizeBefore, sizeAfter, pk.Data.Length - 18 );

                        if ( sizeAfter != (pk.Data.Length - 18) )
                        {
							if (offlineDataPacketRetryCount < 3)
							{
								SendOfflinePacketResponse(packetId, false);
								++offlineDataPacketRetryCount;
							}
							else
							{
								offlineDataPacketRetryCount = 0;
								Callback.onFinishedOfflineDownload(this, false);
							}
							return;
                        }

                        byte[] oData = pk.GetBytes( sizeAfter );

                        byte[] strData = Ionic.Zlib.ZlibStream.UncompressBuffer( oData );

                        if ( strData.Length != sizeBefore )
                        {
							if (offlineDataPacketRetryCount < 3)
							{
								SendOfflinePacketResponse(packetId, false);
								++offlineDataPacketRetryCount;
							}
							else
							{
								offlineDataPacketRetryCount = 0;
								Callback.onFinishedOfflineDownload(this, false);
							}
							return;
                        }

                        ByteUtil butil = new ByteUtil( strData );

                        int checksumErrorCount = 0;

                        for ( int i = 0; i < strCount; i++ )
                        {
                            int pageId = butil.GetInt();

                            long timeStart = butil.GetLong();

                            long timeEnd = butil.GetLong();

                            int penTipType = (int)( butil.GetByte() & 0xFF );

                            int color = butil.GetInt();

                            short dotCount = butil.GetShort();

                            long time = timeStart;

                            //System.Console.WriteLine( "pageId : {0}, timeStart : {1}, timeEnd : {2}, penTipType : {3}, color : {4}, dotCount : {5}, time : {6},", pageId, timeStart, timeEnd, penTipType, color, dotCount, time );

                            offlineStroke = new Stroke( section, owner, note, pageId );

                            for ( int j = 0; j < dotCount; j++ )
                            {
                                byte dotChecksum = butil.GetChecksum( 15 );

                                int timeadd = butil.GetByte();

                                time += timeadd;

                                int force = butil.GetShort();

                                int x = butil.GetShort();
                                int y = butil.GetShort();

                                int fx = butil.GetByte();
                                int fy = butil.GetByte();

                                int tx = butil.GetByte();
                                int ty = butil.GetByte();

                                int twist = butil.GetShort();

                                short reserved = butil.GetShort();

                                byte checksum = butil.GetByte();

                                //System.Console.WriteLine( "x : {0}, y : {1}, force : {2}, checksum : {3}, dotChecksum : {4}", tx, ty, twist, checksum, dotChecksum );

                                if ( dotChecksum != checksum )
                                {
                                    // 체크섬 에러 3번 이상이면 에러로 전송 종료
                                    if (checksumErrorCount++ > 1)
                                    {
                                        result.Clear();
                                        Callback.onFinishedOfflineDownload(this, false);
                                        return;
                                    }

                                    continue;
                                }

                                DotTypes dotType;

                                if ( j == 0 )
                                {
                                    dotType = DotTypes.PEN_DOWN;
                                }
                                else if ( j ==  dotCount-1 )
                                {
                                    dotType = DotTypes.PEN_UP;
                                }
                                else
                                {
                                    dotType = DotTypes.PEN_MOVE;
                                }

                                Dot.Builder builder = new Dot.Builder(MaxForce);

                                Dot dot = builder.owner(owner)
                                    .section(section)
                                    .note(note)
                                    .page(pageId)
                                    .timestamp(time)
                                    .coord(x, fx, y, fy)
                                    .force(force)
                                    .dotType(dotType)
                                    .color(color).Build();

                                //offlineStroke.Add( new Dot( owner, section, note, pageId, time, x, y, fx, fy, force, dotType, color ) );
                                offlineFillterForPaper.Put(dot, null );

                            }

                            result.Add( offlineStroke );
                        }

                        var resultSymbol = new List<Symbol>();

                        if (MetadataManager != null)
                        {
                            foreach (var stroke in result)
                            {
                                var symbols = MetadataManager.FindApplicableSymbols(stroke);

                                if (symbols != null && symbols.Count > 0)
                                {
                                    foreach (var symbol in symbols)
                                    {
                                        if (resultSymbol.Where(s => s.Id == symbol.Id).Count() <= 0)
                                        {
                                            resultSymbol.Add(symbol);
                                        }
                                    }
                                }
                            }
                        }

                        SendOfflinePacketResponse( packetId );

						offlineDataPacketRetryCount = 0;
						Callback.onReceiveOfflineStrokes( this, mTotalOfflineStroke, mReceivedOfflineStroke, result.ToArray(), resultSymbol.ToArray());

                        if ( location == 2 )
                        {
                            Callback.onFinishedOfflineDownload( this, true );
                        }

                        #endregion
                    }
                    break;

                case Cmd.OFFLINE_DATA_DELETE_RESPONSE:
                    {
                        Callback.onRemovedOfflineData( this, pk.Result == 0x00 );
                    }
                    break;
                #endregion

                #region firmware response
                case Cmd.FIRMWARE_UPLOAD_RESPONSE:
                    {
                        if ( pk.Result != 0 || pk.GetByteToInt() != 0 )
                        {
                            IsUploading = false;
                            Callback.onReceiveFirmwareUpdateResult( this, false );
                        }
                    }
                    break;

                case Cmd.FIRMWARE_PACKET_REQUEST:
                    {
                        int status = pk.GetByteToInt();
                        int offset = pk.GetInt();

                        ResponseChunkRequest( offset, status != 3 );
                    }
                    break;
                #endregion

                #region Pen Profile

                case Cmd.PEN_PROFILE_RESPONSE:
                    {
                        if (pk.Result == 0x00)
                        {
                            string profileName = pk.GetString(8);

                            byte type = pk.GetByte();

                            PenProfileReceivedCallbackArgs eventArgs = null;

                            if (type == PenProfile.PROFILE_CREATE)
                            {
                                eventArgs = PenProfileCreate(profileName, pk);
                            }
                            else if (type == PenProfile.PROFILE_DELETE)
                            {
                                eventArgs = PenProfileDelete(profileName, pk);
                            }
                            else if (type == PenProfile.PROFILE_INFO)
                            {
                                eventArgs = PenProfileInfo(profileName, pk);
                            }
                            else if (type == PenProfile.PROFILE_READ_VALUE)
                            {
                                eventArgs = PenProfileReadValue(profileName, pk);
                            }
                            else if (type == PenProfile.PROFILE_WRITE_VALUE)
                            {
                                eventArgs = PenProfileWriteValue(profileName, pk);
                            }
                            else if (type == PenProfile.PROFILE_DELETE_VALUE)
                            {
                                eventArgs = PenProfileDeleteValue(profileName, pk);
                            }

                            if (eventArgs != null)
                            {
                                Callback.onPenProfileReceived(this, eventArgs);
                            }
                            else
                            {
                                Callback.onPenProfileReceived(this, new PenProfileReceivedCallbackArgs(PenProfileReceivedCallbackArgs.ResultType.Failed));
                            }
                        }
                        else
                        {
                            Callback.onPenProfileReceived(this, new PenProfileReceivedCallbackArgs(PenProfileReceivedCallbackArgs.ResultType.Failed));
                        }
                    }
                    break;

                #endregion

                case Cmd.ONLINE_DATA_RESPONSE:
                    {
                        bool result = pk.Result == 0x00;

                        Callback.onAvailableNoteAccepted(this, result);
                    }
                    break;

                #region encryption

                case Cmd.ENCRYPTION_CERT_UPDATE_RESPONSE:
                    {
                        int result = pk.GetByteToInt();
                        CertificateUpdateResult resultType;
                        if (result == 0)
                            resultType = CertificateUpdateResult.Success;
                        else if (result == 1)
                            resultType = CertificateUpdateResult.FileCopyFailed;
                        else if (result == 2)
                            resultType = CertificateUpdateResult.FileReplacementFailed;
                        else if (result == 3)
                            resultType = CertificateUpdateResult.InvalidExpirationDate;
                        else if (result == 4)
                            resultType = CertificateUpdateResult.InvalidProtocolVersion;
                        else if (result == 5)
                            resultType = CertificateUpdateResult.InternalProcessingError;
                        else
                            resultType = CertificateUpdateResult.UnknownError;
                        Callback.onReceiveCertificateUpdateResult(this, resultType);
                    }
                    break;

                case Cmd.ENCRYPTION_CERT_DELETE_RESPONSE:
                    {
                        int result = pk.GetByteToInt();
                        CertificateDeleteResult resultType;
                        if (result == 0)
                            resultType = CertificateDeleteResult.Success;
                        else if (result == 1)
                            resultType = CertificateDeleteResult.NoCertificate;
                        else if (result == 2)
                            resultType = CertificateDeleteResult.InvalidSerialCode;
                        else if (result == 3)
                            resultType = CertificateDeleteResult.FileDeleteFailed;
                        else if (result == 4)
                            resultType = CertificateDeleteResult.InvalidProtocolVersion;
                        else
                            resultType = CertificateDeleteResult.UnknownError;
                        Callback.onReceiveCertificateDeleteResult(this, resultType);
                    }
                    break;

                case Cmd.ENCRYPTION_KEY_RESPONSE:
                    {
                        if (pk.Result == 0x00)
                        {
                            int result = pk.GetByteToInt();
                            if (result == 0x00)
                            {
                                byte[] data = pk.GetBytes(256);

                                if (rsaKeys != null)
                                {
                                    byte[] aesKey = RSACipher.Decrypt(rsaKeys, data);

                                    if (aesKey == null || aesKey.Length != 32)
                                    {
                                        Callback.onSecureCommunicationFailureOccurred(this, SecureCommunicationFailureReason.InvalidPrivateKey);
                                        this.Clean();
                                    }
                                    else
                                    {
                                        doGetAesKey = true;
                                        aes256Cipher = new AES256Cipher(aesKey);
                                        ReqPenStatus();
                                    }
                                }
                                else
                                {
                                    Callback.onSecureCommunicationFailureOccurred(this, SecureCommunicationFailureReason.NoPrivateKey);
                                    this.Clean();
                                }
                            }
                            else
                            {
                                Callback.onSecureCommunicationFailureOccurred(this, SecureCommunicationFailureReason.UnknownError);
                                this.Clean();
                            }
                        }
                        else
                        {
                            Callback.onSecureCommunicationFailureOccurred(this, SecureCommunicationFailureReason.UnknownError);
                            this.Clean();
                        }
                    }
                    break;

                case Cmd.ENCRYPTION_ONLINE_PEN_DOT_EVENT:
                case Cmd.ENCRYPTION_ONLINE_PAPER_INFO_EVENT:
                    {
                        if (aes256Cipher == null)
                        {
                            Callback.onSecureCommunicationFailureOccurred(this, SecureCommunicationFailureReason.InvalidPrivateKey);
                            this.Clean();
                            return;
                        }

                        byte[] decrypted = aes256Cipher.Decode(pk.GetBytes(16));
                        if (decrypted != null)
                        {
                            var builder = new Packet.Builder();
                            builder.cmd((int)((Cmd)pk.Cmd == Cmd.ENCRYPTION_ONLINE_PEN_DOT_EVENT ? Cmd.ONLINE_NEW_PEN_DOT_EVENT : Cmd.ONLINE_NEW_PAPER_INFO_EVENT));
                            builder.data(decrypted);
                            builder.result(pk.Result);
                            // 다시 파서로 넘긴다.
                            ParsePacket(builder.Build());
                            break;
                        }
                        else
                        {
                            Callback.onSecureCommunicationFailureOccurred(this, SecureCommunicationFailureReason.InvalidPrivateKey);
                            this.Clean();
                        }
                    }
                    break;

                case Cmd.ENCRYPTION_OFFLINE_PACKET_REQUEST:
                    {
                        if (aes256Cipher == null)
                        {
                            Callback.onSecureCommunicationFailureOccurred(this, SecureCommunicationFailureReason.InvalidPrivateKey);
                            this.Clean();
                            return;
                        }

                        byte[] data = pk.Data;

                        if ((data.Length - 9) % 16 != 0)
                        {
                            Callback.onSecureCommunicationFailureOccurred(this, SecureCommunicationFailureReason.UnknownError);
                            return;
                        }

                        var byteUtil = new ByteUtil();
                        byteUtil.Put(pk.GetBytes(8));

                        int paddingLength = pk.GetByteToInt();

                        while(pk.CheckMoreData())
                        {
                            byte[] decrypted = aes256Cipher.Decode(pk.GetBytes(16));
                            if (decrypted != null)
                            {
                                byteUtil.Put(decrypted);
                            }
                            else
                            {
                                Callback.onSecureCommunicationFailureOccurred(this, SecureCommunicationFailureReason.InvalidPrivateKey);
                                this.Clean();
                                return;
                            }
                        }

                        var builder = new Packet.Builder();
                        builder.cmd((int)(Cmd.OFFLINE_PACKET_REQUEST));
                        builder.data(byteUtil.GetBytes(byteUtil.WritePosition - paddingLength));
                        builder.result(pk.Result);

                        // 다시 파서로 넘긴다.
                        ParsePacket(builder.Build());
                    }
                    break;

                #endregion
                default:
                    break;
            }

            System.Console.WriteLine();    
        }

        #region Pen Profile Response

        private PenProfileReceivedCallbackArgs PenProfileCreate(string profileName, Packet packet)
        {
            byte status = packet.GetByte();
            return new PenProfileCreateCallbackArgs(profileName, status);
        }

        private PenProfileReceivedCallbackArgs PenProfileDelete(string profileName, Packet packet)
        {
            byte status = packet.GetByte();
            return new PenProfileDeleteCallbackArgs(profileName, status);
        }

        private PenProfileReceivedCallbackArgs PenProfileInfo(string profileName, Packet packet)
        {
            byte status = packet.GetByte();

            var args = new PenProfileInfoCallbackArgs(profileName, status);

            if (status == 0x00)
            {
                args.TotalSectionCount = packet.GetShort();
                args.SectionSize = packet.GetShort();
                args.UseSectionCount = packet.GetShort();
                args.UseKeyCount = packet.GetShort();
            }
            return args;
        }

        private PenProfileReceivedCallbackArgs PenProfileReadValue(string profileName, Packet packet)
        {
            int count = packet.GetByte();

            var args = new PenProfileReadValueCallbackArgs(profileName);

            try
            {
                for (int i = 0; i < count; ++i)
                {
                    var result = new PenProfileReadValueCallbackArgs.ReadValueResult();
                    result.Key = packet.GetString(16);
                    result.Status = packet.GetByte();
                    int dataSize = packet.GetShort();
                    result.Data = packet.GetBytes(dataSize);
                    args.Data.Add(result);
                }
            }
            catch (Exception exp)
            {
                Debug.WriteLine(exp.StackTrace);
            }

            return args;
        }

        private PenProfileReceivedCallbackArgs PenProfileWriteValue(string profileName, Packet packet)
        {
            int count = packet.GetByte();

            var args = new PenProfileWriteValueCallbackArgs(profileName);

            try
            {
                for (int i = 0; i < count; ++i)
                {
                    var result = new PenProfileWriteValueCallbackArgs.WriteValueResult();
                    result.Key = packet.GetString(16);
                    result.Status = packet.GetByte();
                    args.Data.Add(result);
                }
            }
            catch (Exception exp)
            {
                Debug.WriteLine(exp.StackTrace);
            }

            return args;
        }

        private PenProfileReceivedCallbackArgs PenProfileDeleteValue(string profileName, Packet packet)
        {
            int count = packet.GetByte();

            var args = new PenProfileDeleteValueCallbackArgs(profileName);

            try
            {
                for (int i = 0; i < count; ++i)
                {
                    var result = new PenProfileDeleteValueCallbackArgs.DeleteValueResult();
                    result.Key = packet.GetString(16);
                    result.Status = packet.GetByte();
                    args.Data.Add(result);
                }
            }
            catch (Exception exp)
            {
                Debug.WriteLine(exp.StackTrace);
            }

            return args;
        }

        #endregion

        private bool IsBeforeMiddle = false;

		private bool IsStartWithPaperInfo = false;

		private bool IsDownCreated = false;

		private long SessionTs = -1;

		private int EventCount = -1;

		private void CheckEventCount(int ecount)
		{
			if (ecount - EventCount != 1 && (ecount != 0 || EventCount != 255))
			{
				// 이벤트 카운트 오류
				Dot errorDot = null;

				if (mPrevDot != null)
				{
					errorDot = mPrevDot.Clone();
					errorDot.DotType = DotTypes.PEN_ERROR;
				}

				if (ecount - EventCount > 1)
				{
					string extraData = string.Format("missed event count {0}-{1}", EventCount + 1, ecount - 1);
					Callback.onErrorDetected(this, ErrorType.InvalidEventCount, SessionTs, errorDot, extraData, null);
				}
				else if (ecount < EventCount)
				{
					string extraData = string.Format("invalid event count {0},{1}", EventCount, ecount);
					Callback.onErrorDetected(this, ErrorType.InvalidEventCount, SessionTs, errorDot, extraData, null);
				}
			}

			EventCount = ecount;
		}


		private void ParseDotPacket( Cmd cmd, Packet pk )
		{
			switch (cmd)
			{
				case Cmd.ONLINE_NEW_PEN_DOWN_EVENT:
					{
						if (IsStartWithDown && IsBeforeMiddle && mPrevDot != null)
						{
							MakeUpDot();
						}

						int ecount = pk.GetByteToInt();

						CheckEventCount(ecount);

						IsStartWithDown = true;

						mTime = pk.GetLong();

						SessionTs = mTime;

						IsBeforeMiddle = false;
						IsStartWithPaperInfo = false;

						IsDownCreated = false;

						mDotCount = 0;

						mPenTipType = pk.GetByte() == 0x00 ? PenTipType.Normal : PenTipType.Eraser;
						mPenTipColor = pk.GetInt();

						mPrevDot = null;
					}
					break;
				case Cmd.ONLINE_NEW_PEN_UP_EVENT:
					{
						int ecount = pk.GetByteToInt();

						CheckEventCount(ecount);

						long timestamp = pk.GetLong();

						int dotCount = pk.GetShort();
						int totalImageCount = pk.GetShort();
						int procImageCount = pk.GetShort();
						int succImageCount = pk.GetShort();
						int sendImageCount = pk.GetShort();

						if (IsStartWithDown && IsBeforeMiddle && mPrevDot != null)
						{
							var udot = mPrevDot.Clone();
							udot.DotType = DotTypes.PEN_UP;

							ImageProcessingInfo imageInfo = null;

							if (!IsDownCreated)
							{
								imageInfo = new ImageProcessingInfo
								{
									DotCount = dotCount,
									Total = totalImageCount,
									Processed = procImageCount,
									Success = succImageCount,
									Transferred = sendImageCount
								};
							}

							ProcessDot(udot, imageInfo);
						}
						else if (!IsStartWithDown && !IsBeforeMiddle)
						{
							// 즉 다운업(무브없이) 혹은 업만 들어올 경우 UP dot을 보내지 않음
							Callback.onErrorDetected(this, ErrorType.MissingPenDownPenMove, -1, null, null, null);
						}
						else if (!IsBeforeMiddle)
						{
							// 무브없이 다운-업만 들어올 경우 UP dot을 보내지 않음
							Callback.onErrorDetected(this, ErrorType.MissingPenMove, SessionTs, null, null, null);
						}

						mTime = -1;
						SessionTs = -1;

						IsStartWithDown = false;
						IsBeforeMiddle = false;
						IsStartWithPaperInfo = false;
						IsDownCreated = false;

						mDotCount = 0;

						mPrevDot = null;
					}
					break;
				case Cmd.ONLINE_PEN_UPDOWN_EVENT:
					bool IsDown = pk.GetByte() == 0x00;

					if (IsDown)
					{
						if (IsStartWithDown && IsBeforeMiddle && mPrevDot != null)
						{
							// 펜업이 넘어오지 않음
							//var errorDot = mPrevDot.Clone();
							//errorDot.DotType = DotTypes.PEN_ERROR;
							//Callback.onErrorDetected(this, ErrorType.MissingPenUp, SessionTs, errorDot, null, null);

							MakeUpDot();
						}

						IsStartWithDown = true;

						mTime = pk.GetLong();

						SessionTs = mTime;
					}
					else
					{
						if (IsStartWithDown && IsBeforeMiddle && mPrevDot != null)
						{
							MakeUpDot(false);
						}
						else if (!IsStartWithDown && !IsBeforeMiddle)
						{
							Callback.onErrorDetected(this, ErrorType.MissingPenDownPenMove, -1, null, null, null);
						}
						else if (!IsBeforeMiddle)
						{
							// 무브없이 다운-업만 들어올 경우 UP dot을 보내지 않음
							Callback.onErrorDetected(this, ErrorType.MissingPenMove, SessionTs, null, null, null);
						}

						IsStartWithDown = false;

						mTime = -1;

						SessionTs = -1;
					}

					IsBeforeMiddle = false;
					IsStartWithPaperInfo = false;
					IsDownCreated = false;

					mDotCount = 0;

					mPenTipType = pk.GetByte() == 0x00 ? PenTipType.Normal : PenTipType.Eraser;
					mPenTipColor = pk.GetInt();

					mPrevDot = null;
					
					break;

				case Cmd.ONLINE_PEN_DOT_EVENT:
				case Cmd.ONLINE_NEW_PEN_DOT_EVENT:
					{
						if (cmd == Cmd.ONLINE_NEW_PEN_DOT_EVENT)
						{
							int ecount = pk.GetByteToInt();

							CheckEventCount(ecount);
						}

						int timeadd = pk.GetByte();

						mTime += timeadd;

						int force = pk.GetShort();

						int x = pk.GetUShort();
						int y = pk.GetUShort();

						int fx = pk.GetByte();
						int fy = pk.GetByte();

						Dot dot = null;

						if (!HoverMode && !IsStartWithDown)
						{
							if (!IsStartWithPaperInfo)
							{
								//펜 다운 없이 페이퍼 정보 없고 무브가 오는 현상(다운 - 무브 - 업 - 다운X - 무브)
								Callback.onErrorDetected(this, ErrorType.MissingPenDown, -1, null, null, null);
							}
							else
							{
								mTime = Time.GetUtcTimeStamp();

								SessionTs = mTime;
								var errorDot = MakeDot(mCurOwner, mCurSection, mCurNote, mCurPage, mTime, x, y, fx, fy, force, DotTypes.PEN_ERROR, mPenTipColor);
								Callback.onErrorDetected(this, ErrorType.MissingPenDown, SessionTs, errorDot, null, null);
								//펜 다운 없이 페이퍼 정보 있고 무브가 오는 현상(다운 - 무브 - 업 - 다운X - 무브)
								IsStartWithDown = true;
								IsDownCreated = true;
							}
						}

						if (HoverMode && !IsStartWithDown && IsStartWithPaperInfo)
						{
							dot = MakeDot(mCurOwner, mCurSection, mCurNote, mCurPage, mTime, x, y, fx, fy, force, DotTypes.PEN_HOVER, mPenTipColor);
						}
						else if (IsStartWithDown)
						{
							if (IsStartWithPaperInfo)
							{
								dot = MakeDot(mCurOwner, mCurSection, mCurNote, mCurPage, mTime, x, y, fx, fy, force, mDotCount == 0 ? DotTypes.PEN_DOWN : DotTypes.PEN_MOVE, mPenTipColor);
							}
							else
							{
								//펜 다운 이후 페이지 체인지 없이 도트가 들어왔을 경우
								Callback.onErrorDetected(this, ErrorType.MissingPageChange, SessionTs, null, null, null);
							}
						}

						if (dot != null)
						{
							ProcessDot(dot, null);
						}

						IsBeforeMiddle = true;
						mPrevDot = dot;
						mDotCount++;
					}
					break;

				case Cmd.ONLINE_PAPER_INFO_EVENT:
				case Cmd.ONLINE_NEW_PAPER_INFO_EVENT:
					{
						if (cmd == Cmd.ONLINE_NEW_PAPER_INFO_EVENT)
						{
							int ecount = pk.GetByteToInt();

							CheckEventCount(ecount);
						}

						// 미들도트 중에 페이지가 바뀐다면 강제로 펜업을 만들어 준다.
						if (IsStartWithDown && IsBeforeMiddle && mPrevDot != null)
						{
							MakeUpDot(false);
						}

						byte[] rb = pk.GetBytes(4);

						mCurSection = (int)(rb[3] & 0xFF);
						mCurOwner = ByteConverter.ByteToInt(new byte[] { rb[0], rb[1], rb[2], (byte)0x00 });
						mCurNote = pk.GetInt();
						mCurPage = pk.GetInt();

						mDotCount = 0;

						IsStartWithPaperInfo = true;
					}
					break;
				case Cmd.ONLINE_PEN_ERROR_EVENT:
				case Cmd.ONLINE_NEW_PEN_ERROR_EVENT:
					{
						if (cmd == Cmd.ONLINE_NEW_PEN_ERROR_EVENT)
						{
							int ecount = pk.GetByteToInt();

							CheckEventCount(ecount);
						}

						int timeadd = pk.GetByteToInt();
						mTime += timeadd;

						int force = pk.GetShort();
						int brightness = pk.GetByteToInt();
						int exposureTime = pk.GetByteToInt();
						int ndacProcessTime = pk.GetByteToInt();
						int labelCount = pk.GetShort();
						int ndacErrorCode = pk.GetByteToInt();
						int classType = pk.GetByteToInt();
						int errorCount = pk.GetByteToInt();

						ImageProcessErrorInfo newInfo = new ImageProcessErrorInfo
						{
							Timestamp = mTime,
							Force = force,
							Brightness = brightness,
							ExposureTime = exposureTime,
							ProcessTime = ndacProcessTime,
							LabelCount = labelCount,
							ErrorCode = ndacErrorCode,
							ClassType = classType,
							ErrorCount = errorCount
						};

						Dot errorDot = null;

						if (mPrevDot != null)
						{
							errorDot = mPrevDot.Clone();
							errorDot.DotType = DotTypes.PEN_UP;
						}

						Callback.onErrorDetected(this, ErrorType.ImageProcessingError, SessionTs, errorDot, null, newInfo);
					}
					break;
			}
		}

		private void ProcessDot(Dot dot, object obj = null)
		{
			dotFilterForPaper.Put(dot, obj);
		}

        private Stroke curStroke;

		private void SendDotReceiveEvent(Dot dot, object obj)
		{
            if (curStroke == null || dot.DotType == DotTypes.PEN_DOWN)
            {
                curStroke = new Stroke(dot.Section, dot.Owner, dot.Note, dot.Page);
            }

            curStroke.Add(dot);

            Callback.onReceiveDot( this, dot, obj as ImageProcessingInfo );

            if (dot.DotType == DotTypes.PEN_UP && MetadataManager != null)
            {
                var symbols = MetadataManager.FindApplicableSymbols(curStroke);

                if (symbols != null && symbols.Count > 0)
                {
                    Callback.onSymbolDetected(this, symbols);
                }
            }
        }

		private Stroke offlineStroke;

		private void AddOfflineFilteredDot(Dot dot, object obj)
		{
			offlineStroke.Add(dot);
		}

		private void ParseOnlineDataRequest(Packet pk)
        {
            int index = pk.GetInt();
            byte count = pk.GetByte();

            // 과거에 받았던 인덱스가 다시 올경우 무시
            if (index <= mPrevIndex)
            {
                ResponseOnlineData(index, count);
                return;
            }

            mPrevCount = count;
            mPrevIndex = index;

            for (int i = 0; i < mPrevCount; i++)
            {
                byte type = pk.GetByte();

                switch (type)
                {
                    case 0x10:
                        IsStartWithDown = true;
                        mDotCount = 0;
                        mTime = pk.GetLong();
                        mPenTipType = pk.GetByte() == 0x00 ? PenTipType.Normal : PenTipType.Eraser;
                        mPenTipColor = pk.GetInt();
                        break;

                    case 0x20:
                        long penuptime = pk.GetLong();
                        int total = pk.GetShort();
                        int processed = pk.GetShort();
                        int success = pk.GetShort();
                        int transferred = pk.GetShort();
                        if (mPrevDot != null)
                        {
                            mPrevDot.DotType = DotTypes.PEN_UP;
                            ImageProcessingInfo info = new ImageProcessingInfo { Total = total, Processed = processed, Success = success, Transferred = transferred };
							ProcessDot(mPrevDot, info);
                            //Callback.onReceiveDot(this, mPrevDot, info);
                        }
                        break;

                    case 0x30:
                        byte[] rb = pk.GetBytes(4);
                        mCurSection = (int)(rb[3] & 0xFF);
                        mCurOwner = ByteConverter.ByteToInt(new byte[] { rb[0], rb[1], rb[2], (byte)0x00 });
                        mCurNote = pk.GetInt();
                        mCurPage = pk.GetInt();
                        break;

                    case 0x40:

                        int timeadd = pk.GetByte();

                        mTime += timeadd;

                        int force = pk.GetShort();

                        int x = pk.GetUShort();
                        int y = pk.GetUShort();

                        int fx = pk.GetByte();
                        int fy = pk.GetByte();

                        Dot dot = null;

                        if (HoverMode && !IsStartWithDown)
                        {
                            dot = MakeDot(mCurOwner, mCurSection, mCurNote, mCurPage, mTime, x, y, fx, fy, force, DotTypes.PEN_HOVER, mPenTipColor);
                        }
                        else if (IsStartWithDown)
                        {
                            dot = MakeDot(mCurOwner, mCurSection, mCurNote, mCurPage, mTime, x, y, fx, fy, force, mDotCount == 0 ? DotTypes.PEN_DOWN : DotTypes.PEN_MOVE, mPenTipColor);
                        }
                        else
                        {
                            //오류
                        }

                        if (dot != null)
                        {
							ProcessDot(mPrevDot);
                            //Callback.onReceiveDot(this, dot, null);
                        }

                        mPrevDot = dot;
                        mDotCount++;
                        break;
                }
            }

            //애크를 던지자
            ResponseOnlineData(mPrevIndex, mPrevCount);
        }

        private void ResponseOnlineData(int index, byte count)
        {
            ByteUtil bf = new ByteUtil(Escape);

            bf.Put(Const.PK_STX, false)
              .Put((byte)Cmd.ONLINE_PEN_DATA_RESPONSE)
              .Put((byte)0x00)
              .PutShort(5)
              .PutInt(index)
              .Put(count)
              .Put(Const.PK_ETX, false);

            Send(bf);
        }

		private void MakeUpDot(bool isError = true)
		{
			if (isError)
			{
				var errorDot = mPrevDot.Clone();
				errorDot.DotType = DotTypes.PEN_ERROR;
				Callback.onErrorDetected(this, ErrorType.MissingPenUp, SessionTs, errorDot, null, null);
			}

			var audot = mPrevDot.Clone();
			audot.DotType = DotTypes.PEN_UP;
			ProcessDot(audot, null);
		}

		private byte[] Escape( byte input )
        {
            if ( input == Const.PK_STX || input == Const.PK_ETX || input == Const.PK_DLE  )
            {
                return new byte[] { Const.PK_DLE, (byte)(input ^ 0x20)  };
            }
            else
            {
                return new byte[] { input };
            }
        }

        private bool Send( ByteUtil bf )
        {
            bool result = Write( bf.ToArray() );

            bf.Clear();
            bf = null;

            return result;
        }

        private void ReqVersion()
        {
            ByteUtil bf = new ByteUtil( Escape );

            Assembly assemObj = Assembly.GetExecutingAssembly();
            Version v = assemObj.GetName().Version; // 현재 실행되는 어셈블리..dll의 버전 가져오기

			byte[] StrAppVersion = Encoding.UTF8.GetBytes(String.Format("{0}.{1}.{2}.{3}", v.Major, v.Minor, v.Build, v.Revision));
			byte[] StrProtocolVersion = Encoding.UTF8.GetBytes(SupportedProtocolVersion);

			bf.Put(Const.PK_STX, false)
			  .Put((byte)Cmd.VERSION_REQUEST)
			  .PutShort(42)
			  .PutNull(16)
			  .Put(0x12)
			  .Put(0x01)
			  .Put(StrAppVersion, 16)
			  .Put(StrProtocolVersion, 8)
			  .Put(Const.PK_ETX, false);

			Send( bf );
        }

        #region password

        /// <summary>
        /// Change the password of device.
        /// </summary>
        /// <param name="oldPassword">Current password</param>
        /// <param name="newPassword">New password</param>
        /// <returns>true if the request is accepted; otherwise, false.</returns>
        public bool ReqSetUpPassword( string oldPassword, string newPassword = "" )
        {
			if (oldPassword == null || newPassword == null)
				return false;

			if (oldPassword.Equals(DEFAULT_PASSWORD))
				return false;
			if (newPassword.Equals(DEFAULT_PASSWORD))
				return false;

			this.newPassword = newPassword;

			byte[] oPassByte = Encoding.UTF8.GetBytes( oldPassword );
            byte[] nPassByte = Encoding.UTF8.GetBytes( newPassword );

            ByteUtil bf = new ByteUtil( Escape );
            bf.Put( Const.PK_STX, false )
              .Put( (byte)Cmd.PASSWORD_CHANGE_REQUEST )
              .PutShort( 33 )
              .Put( (byte)( newPassword == "" ? 0 : 1 ) )
              .Put( oPassByte, 16 )
              .Put( nPassByte, 16 )
              .Put( Const.PK_ETX, false );

            return Send( bf );
        }

        /// <summary>
        /// Input password if device is locked.
        /// </summary>
        /// <param name="password">Specifies the password for authentication. Password is a string</param>
        /// <returns>true if the request is accepted; otherwise, false.</returns>
        public bool ReqInputPassword( string password )
        {
			if (password == null)
				return false;

			if (password.Equals(DEFAULT_PASSWORD))
				return false;

            byte[] bStrByte = Encoding.UTF8.GetBytes( password );

            ByteUtil bf = new ByteUtil( Escape );
            bf.Put( Const.PK_STX, false )
              .Put( (byte)Cmd.PASSWORD_REQUEST )
              .PutShort( 16 )
              .Put( bStrByte, 16 )
              .Put( Const.PK_ETX, false );

            return Send( bf );
        }

        #endregion

        #region pen setup

        /// <summary>
        /// Request the status of pen.
        /// If you requested, you can receive result by PenCommV2Callbacks.onReceivedPenStatus method.
        /// </summary>
        /// <returns>true if the request is accepted; otherwise, false.</returns>
        public bool ReqPenStatus()
        {
            ByteUtil bf = new ByteUtil();

            bf.Put( Const.PK_STX )
                .Put( (byte)Cmd.SETTING_INFO_REQUEST )
                .PutShort( 0 )
                .Put( Const.PK_ETX );

            return Send( bf );
        }

        public enum SettingType : byte { Timestamp = 1, AutoPowerOffTime = 2, PenCapOff = 3, AutoPowerOn = 4, Beep = 5, Hover = 6, OfflineData = 7, LedColor = 8, Sensitivity = 9, UsbMode = 10, DownSampling = 11, BtLocalName = 12, FscSensitivity = 13, DataTransmissionType = 14, BeepAndLight = 16 };

        private bool RequestChangeSetting( SettingType stype, object value )
        {
            ByteUtil bf = new ByteUtil(Escape);

            bf.Put( Const.PK_STX, false ).Put( (byte)Cmd.SETTING_CHANGE_REQUEST );

            switch ( stype )
            {
                case SettingType.Timestamp:
                    bf.PutShort( 9 ).Put( (byte)stype ).PutLong( (long)value );
                    break;

                case SettingType.AutoPowerOffTime:
                    bf.PutShort( 3 ).Put( (byte)stype ).PutShort( (short)value );
                    break;

                case SettingType.LedColor:
                    bf.PutShort( 5 ).Put( (byte)stype ).PutInt( (int)value );
                    break;

                case SettingType.PenCapOff:
                case SettingType.AutoPowerOn:
                case SettingType.Beep:
                case SettingType.Hover:
                case SettingType.OfflineData:
                case SettingType.DownSampling:
                    bf.PutShort( 2 ).Put( (byte)stype ).Put( (byte)( (bool)value ? 1 : 0 ) );
                    break;
                case SettingType.Sensitivity:
                    bf.PutShort( 2 ).Put( (byte)stype ).Put( (byte)( (short)value ) );
                    break;
                case SettingType.UsbMode:
                    bf.PutShort( 2 ).Put( (byte)stype ).Put((byte)value );
                    break;
                case SettingType.BtLocalName:
                    byte[] StrByte = Encoding.UTF8.GetBytes( (string)value );
                    bf.PutShort(18).Put((byte)stype).Put(16).Put(StrByte, 16);
                    break;
                case SettingType.FscSensitivity:
                    bf.PutShort(2).Put( (byte)stype ).Put((byte)( (short)value) );
                    break;
                case SettingType.DataTransmissionType:
                    bf.PutShort(2).Put( (byte)stype ).Put( (byte)value );
                    break;
                case SettingType.BeepAndLight:
                    bf.PutShort(2).Put((byte)stype).Put((byte)0);
                    break;
            }

            bf.Put( Const.PK_ETX, false );

            return Send( bf );
        }

        /// <summary>
        /// Sets the RTC timestamp.
        /// </summary>
        /// <param name="timetick">milisecond timestamp tick (from 1970-01-01)</param>
        /// <returns>true if the request is accepted; otherwise, false.</returns>
        public bool ReqSetupTime( long timetick )
        {
            return RequestChangeSetting( SettingType.Timestamp, timetick );
        }

        /// <summary>
        /// Sets the value of the auto shutdown time property that if pen stay idle, shut off the pen.
        /// </summary>
        /// <param name="minute">minute of maximum idle time, staying power on (0~)</param>
        /// <returns>true if the request is accepted; otherwise, false.</returns>
        public bool ReqSetupPenAutoShutdownTime( short minute )
        {
            return RequestChangeSetting( SettingType.AutoPowerOffTime, minute );
        }

        /// <summary>
        /// Sets the status of the power control by cap on property.
        /// </summary>
        /// <param name="enable">true if you want to use, otherwise false.</param>
        /// <returns>true if the request is accepted; otherwise, false.</returns>
        public bool ReqSetupPenCapPower( bool enable )
        {
            return RequestChangeSetting( SettingType.PenCapOff, enable );
        }

        /// <summary>
        /// Sets the status of the auto power on property that if write the pen, turn on when pen is down.
        /// </summary>
        /// <param name="enable">true if you want to use, otherwise false.</param>
        /// <returns>true if the request is accepted; otherwise, false.</returns>
        public bool ReqSetupPenAutoPowerOn( bool enable )
        {
            return RequestChangeSetting( SettingType.AutoPowerOn, enable );
        }

        /// <summary>
        /// Sets the status of the beep property.
        /// </summary>
        /// <param name="enable">true if you want to listen sound of pen, otherwise false.</param>
        /// <returns>true if the request is accepted; otherwise, false.</returns>
        public bool ReqSetupPenBeep( bool enable )
        {
            return RequestChangeSetting( SettingType.Beep, enable );
        }

        /// <summary>
        /// Sets the hover mode.
        /// </summary>
        /// <param name="enable">true if you want to enable hover mode, otherwise false.</param>
        /// <returns>true if the request is accepted; otherwise, false.</returns>
        private bool ReqSetupHoverMode( bool enable )
        {
            return RequestChangeSetting( SettingType.Hover, enable );
        }

        /// <summary>
        /// Sets the offline data option whether save offline data or not.
        /// </summary>
        /// <param name="enable">true if you want to enable offline mode, otherwise false.</param>
        /// <returns>true if the request is accepted; otherwise, false.</returns>
        public bool ReqSetupOfflineData( bool enable )
        {
            return RequestChangeSetting( SettingType.OfflineData, enable );
        }

        /// <summary>
        /// Sets the color of LED.
        /// </summary>
        /// <param name="rgbcolor">integer type color formatted 0xAARRGGBB</param>
        /// <returns>true if the request is accepted; otherwise, false.</returns>
        public bool ReqSetupPenColor( int rgbcolor )
        {
            return RequestChangeSetting( SettingType.LedColor, rgbcolor);
        }

        /// <summary>
        /// @deprecated This feature is deprecated. so this feature does not work.\n
        /// Sets the value of the pen's sensitivity property that controls the force sensor(r-type) of pen.
        /// </summary>
        /// <param name="level">the value of sensitivity. (0~4, 0 means maximum sensitivity)</param>
        /// <returns>true if the request is accepted; otherwise, false.</returns>
        public bool ReqSetupPenSensitivity( short level)
        {
            //return RequestChangeSetting( SettingType.Sensitivity, level);
            return false;
        }

        /// <summary>
        /// Sets the status of usb mode property that determine if usb mode is disk or bulk.
        /// You can choose between Disk mode, which is used as a removable disk, and Bulk mode, which is capable of high volume data communication, when connected with usb
        /// </summary>
        /// <param name="mode">enum of UsbMode</param>
        /// <returns>true if the request is accepted; otherwise, false.</returns>
        public bool ReqSetupUsbMode( UsbMode mode )
        {
            return RequestChangeSetting( SettingType.UsbMode, mode );
        }

        /// <summary>
        /// Sets the status of the down sampling property.
        /// Downsampling is a function of avoiding unnecessary data communication by omitting coordinates at the same position.
        /// </summary>
        /// <param name="enable">true if you want to enable down sampling, otherwise false.</param>
        /// <returns>true if the request is accepted; otherwise, false.</returns>
        public bool ReqSetupDownSampling( bool enable)
        {
            return RequestChangeSetting( SettingType.DownSampling, enable );
        }

        /// <summary>
        /// Sets the local name of the bluetooth device property.
        /// </summary>
        /// <param name="btLocalName">Bluetooth local name to set</param>
        /// <returns>true if the request is accepted; otherwise, false.</returns>
        public bool ReqSetupBtLocalName( string btLocalName )
        {
            return RequestChangeSetting( SettingType.BtLocalName, btLocalName );
        }

        /// <summary>
        /// @deprecated This feature is deprecated. so this feature does not work.\n
        /// Sets the value of the pen's sensitivity property that controls the force sensor(c-type) of pen.
        /// </summary>
        /// <param name="level">the value of sensitivity. (0~4, 0 means maximum sensitivity)</param>
        /// <returns>true if the request is accepted; otherwise, false.</returns>
        public bool ReqSetupPenFscSensitivity( short level )
        {
            return false;
            //return RequestChangeSetting( SettingType.FscSensitivity, level);
        }

        /// <summary>
        /// Sets the status of data transmission type property that determine if data transmission type is event or request-response.
        /// </summary>
        /// <param name="type">enum of DataTransmissionType</param>
        /// <returns>true if the request is accepted; otherwise, false.</returns>
        public bool ReqSetupDataTransmissionType( DataTransmissionType type )
        {
            return RequestChangeSetting( SettingType.DataTransmissionType, type );
        }

        /// <summary>
        /// Request Beeps and light on.
        /// </summary>
        /// <returns>true if the request is accepted; otherwise, false.</returns>
        public bool ReqBeepAndLight()
        {
            return RequestChangeSetting(SettingType.BeepAndLight, null);
        }

        #endregion

        #region using note

        private bool SendAddUsingNote( int sectionId = -1, int ownerId = -1, int[] noteIds = null )
        {
            ByteUtil bf = new ByteUtil(Escape);

            bf.Put( Const.PK_STX, false )
              .Put( (byte)Cmd.ONLINE_DATA_REQUEST );

            if ( sectionId >= 0 && ownerId > 0 && noteIds == null )
            {
                bf.PutShort( 2 + 8 )
                  .PutShort( 1 )
                  .Put( GetSectionOwnerByte( sectionId, ownerId ) )
                  .Put( 0xFF ).Put( 0xFF ).Put( 0xFF ).Put( 0xFF );
            }
            else if ( sectionId >= 0 && ownerId > 0 && noteIds != null )
            {
                short length = (short)( 2 + ( noteIds.Length * 8 ) );

                bf.PutShort( length )
                  .PutShort( (short)noteIds.Length );

                foreach ( int item in noteIds )
                {
                    bf.Put( GetSectionOwnerByte( sectionId, ownerId ) )
                    .PutInt( item );
                }
            }
            else
            {
                bf.PutShort( 2 )
                  .Put( 0xFF )
                  .Put( 0xFF );
            }

            bf.Put( Const.PK_ETX, false );

            return Send( bf );
        }

		private bool SendAddUsingNote(int[] sectionId, int[] ownerId)
		{
			ByteUtil bf = new ByteUtil(Escape);

			bf.Put(Const.PK_STX, false)
			  .Put((byte)Cmd.ONLINE_DATA_REQUEST);

			bf.PutShort((short)(2 + sectionId.Length * 8))
				.PutShort((short)sectionId.Length);
			for (int i = 0; i < sectionId.Length; ++i)
			{
				bf.Put(GetSectionOwnerByte(sectionId[i], ownerId[i]))
				  .Put(0xFF).Put(0xFF).Put(0xFF).Put(0xFF);
			}

			bf.Put(Const.PK_ETX, false);

			return Send(bf);
		}


		/// <summary>
		/// Sets the available notebook type
		/// </summary>
		/// <returns>true if the request is accepted; otherwise, false.</returns>
		public bool ReqAddUsingNote()
        {
            return SendAddUsingNote();
        }

        /// <summary>
        /// Sets the available notebook type
        /// </summary>
        /// <param name="section">The Section Id of the paper</param>
        /// <param name="owner">The Owner Id of the paper</param>
        /// <param name="notes">The array of Note Id list</param>
        public bool ReqAddUsingNote( int section, int owner, int[] notes = null )
        {
            return SendAddUsingNote( section, owner, notes );
        }

		/// <summary>
		/// Set the available notebook type lits
		/// </summary>
		/// <param name="section">The array of section Id of the paper list</param>
		/// <param name="owner">The array of owner Id of the paper list</param>
		/// <returns></returns>
		public bool ReqAddUsingNote(int[] section, int[] owner)
		{
			return SendAddUsingNote(section, owner);
		}

		#endregion

		#region offline

		/// <summary>
		/// Requests the list of Offline data.
		/// </summary>
		/// <param name="section">The Section Id of the paper</param>
		/// <param name="owner">The Owner Id of the paper</param>
		/// <returns>true if the request is accepted; otherwise, false.</returns>
		public bool ReqOfflineDataList( int section = -1, int owner = -1 )
        {
            ByteUtil bf = new ByteUtil( Escape );

            byte[] pInfo = section > 0 && owner > 0 ? GetSectionOwnerByte( section, owner ) : new byte[] { 0xFF, 0xFF, 0xFF, 0xFF };

            bf.Put( Const.PK_STX, false )
              .Put( (byte)Cmd.OFFLINE_NOTE_LIST_REQUEST )
              .PutShort( 4 )
              .Put( pInfo )
              .Put( Const.PK_ETX, false );

            return Send( bf );
        }

        /// <summary>
        /// Requests the list of Offline data.
        /// </summary>
        /// <param name="section">The Section Id of the paper</param>
        /// <param name="owner">The Owner Id of the paper</param>
        /// <param name="note">The Note Id of the paper</param>
        /// <returns>true if the request is accepted; otherwise, false.</returns>
        public bool ReqOfflineDataPageList( int section, int owner, int note )
        {
            ByteUtil bf = new ByteUtil( Escape );

            bf.Put( Const.PK_STX, false )
              .Put( (byte)Cmd.OFFLINE_PAGE_LIST_REQUEST )
              .PutShort( 8 )
              .Put( GetSectionOwnerByte( section, owner ) )
              .PutInt( note )
              .Put( Const.PK_ETX, false );

            return Send( bf );
        }

        /// <summary>
        /// Requests the transmission of data
        /// </summary>
        /// <param name="section">The Section Id of the paper</param>
        /// <param name="owner">The Owner Id of the paper</param>
        /// <param name="note">The Note Id of the paper</param>
        /// <param name="deleteOnFinished">delete offline data when transmission is finished,</param>
        /// <param name="pages">The number of page</param>
        /// <returns>true if the request is accepted; otherwise, false.</returns>
        public bool ReqOfflineData(int section, int owner, int note, bool deleteOnFinished = true, int[] pages = null)
        {
            byte[] ownerByte = ByteConverter.IntToByte(owner);

            short length = 14;

            length += (short)(pages == null || pages.Length <= 0 ? 0 : pages.Length * 4);

            ByteUtil bf = new ByteUtil(Escape);

            bf.Put(Const.PK_STX, false)
              .Put((byte)Cmd.OFFLINE_DATA_REQUEST)
              .PutShort(length)
              .Put((byte)(deleteOnFinished ? 1 : 2))
              .Put((byte)1)
              .Put(GetSectionOwnerByte(section, owner))
              .PutInt(note)
              .PutInt(pages == null || pages.Length <= 0 ? 0 : pages.Length);

            if (pages != null && pages.Length > 0)
            {
                foreach (int page in pages)
                {
                    bf.PutInt(page);
                }
            }

            bf.Put(Const.PK_ETX, false);

            return Send(bf);
        }

        private void SendOfflinePacketResponse( short index, bool isSuccess = true )
        {
            ByteUtil bf = new ByteUtil( Escape );

            bf.Put(Const.PK_STX, false);
            if (isEncryptedMode)
                bf.Put((byte)Cmd.ENCRYPTION_OFFLINE_PACKET_RESPONSE);
            else
                bf.Put((byte)Cmd.OFFLINE_PACKET_RESPONSE);
            bf.Put( (byte)( isSuccess ? 0 : 1 ) )
              .PutShort( 3 )
              .PutShort( index )
              .Put( 1 )
              .Put( Const.PK_ETX, false );

            Send( bf );
        }

        /// <summary>
        /// Request to remove offline data in device.
        /// </summary>
        /// <param name="section">The Section Id of the paper</param>
        /// <param name="owner">The Owner Id of the paper</param>
        /// <returns>true if the request is accepted; otherwise, false.</returns>
        public bool ReqRemoveOfflineData( int section, int owner, int[] notes )
        {
            ByteUtil bf = new ByteUtil( Escape );

            bf.Put( Const.PK_STX, false )
              .Put( (byte)Cmd.OFFLINE_DATA_DELETE_REQUEST );

            short length = (short)( 5 + ( notes.Length * 4 ) );

            bf.PutShort( length )
              .Put( GetSectionOwnerByte( section, owner ) )
              .Put( (byte)notes.Length );

            foreach ( int noteId in notes )
            {
                bf.PutInt( noteId );
            }

            bf.Put( Const.PK_ETX, false );

            return Send( bf );
        }

        #endregion

        #region firmware

        private Chunk mFwChunk;

        private bool IsUploading = false;

        /// <summary>
        /// Requests the firmware installation
        /// </summary>
        /// <param name="filepath">absolute path of firmware file</param>
        /// <param name="version">version of firmware, this value is string</param>
        /// <returns>true if the request is accepted; otherwise, false.</returns>
        public bool ReqPenSwUpgrade( string filepath, string version )
        {
            if ( IsUploading )
            {
                return false;
            }

            SwUpgradeFailCallbacked = false;
            IsUploading = true;

            mFwChunk = new Chunk(1024);

            bool loaded = mFwChunk.Load( filepath );

            if ( !loaded )
            {
                return false;
            }

            int file_size = mFwChunk.GetFileSize();

            short chunk_count = (short)mFwChunk.GetChunkLength();
            short chunk_size = (short)mFwChunk.GetChunksize();

            byte[] StrVersionByte = Encoding.UTF8.GetBytes( version );

			string deviceName = DeviceName;
			if (deviceName.Equals(F121MG))
				deviceName = F121;

			byte[] StrDeviceByte = Encoding.UTF8.GetBytes( deviceName );

            System.Console.WriteLine( "[FileUploadWorker] file upload => filesize : {0}, packet count : {1}, packet size {2}", file_size, chunk_count, chunk_size );

            ByteUtil bf = new ByteUtil( Escape );

            bf.Put( Const.PK_STX, false )
              .Put( (byte)Cmd.FIRMWARE_UPLOAD_REQUEST )
              .PutShort( 42 )
              .Put( StrDeviceByte, 16 )
              .Put( StrVersionByte, 16 )
              .PutInt( file_size )
              .PutInt( chunk_size )
              .Put( 1 )
              .Put( mFwChunk.GetTotalChecksum() )
              .Put( Const.PK_ETX, false );

            return Send( bf );
        }

        private bool SwUpgradeFailCallbacked = false;
            
        private void ResponseChunkRequest( int offset, bool status = true )
        {
            ByteUtil bf = new ByteUtil( Escape );

            if ( !status || mFwChunk == null || !IsUploading )
            {
                bf.Put(Const.PK_STX, false)
                  .Put((byte)Cmd.FIRMWARE_PACKET_RESPONSE)
                  .Put(0)
                  .PutShort(14)
                  .Put(1)
                  .PutInt(offset)
                  .Put(0)
                  .PutNull(4)
                  .PutNull(4)
                  .Put(Const.PK_ETX, false);

                IsUploading = false;

                Send(bf);

                if (!SwUpgradeFailCallbacked)
                {
                    Callback.onReceiveFirmwareUpdateResult(this, false);
                    SwUpgradeFailCallbacked = true;
                }
            }
            else
            {
                int index = (int)(offset / mFwChunk.GetChunksize());

                System.Console.WriteLine("[FileUploadWorker] ResponseChunkRequest upload => index : {0}", index);

                byte[] data = mFwChunk.Get(index);

                byte[] cdata = Ionic.Zlib.ZlibStream.CompressBuffer( data );

                byte checksum = mFwChunk.GetChecksum( index );

                short dataLength = (short)( cdata.Length + 14 );

                bf.Put( Const.PK_STX, false )
                  .Put( (byte)Cmd.FIRMWARE_PACKET_RESPONSE )
                  .Put( 0 )
                  .PutShort( dataLength )
                  .Put( 0 )
                  .PutInt( offset )
                  .Put( checksum )
                  .PutInt( data.Length )
                  .PutInt( cdata.Length )
                  .Put( cdata )
                  .Put( Const.PK_ETX, false );

                Send(bf);

                Callback.onReceiveFirmwareUpdateStatus(this, mFwChunk.GetChunkLength(), index + 1);
            }
        }

        /// <summary>
        /// To suspend firmware installation.
        /// </summary>
        /// <returns>true if the request is accepted; otherwise, false.</returns>
        public bool SuspendSwUpgrade()
        {
            mFwChunk = null;
            return true;
        }

        #endregion

        #region util
        
        private static byte[] GetSectionOwnerByte( int section, int owner )
        {
            byte[] ownerByte = ByteConverter.IntToByte( owner );
            ownerByte[3] = (byte)section;

            return ownerByte;
        }

		private Dot MakeDot(int owner, int section, int note, int page, long timestamp, int x, int y, int fx, int fy, int force, DotTypes type, int color)
		{
			Dot.Builder builder = new Dot.Builder(MaxForce);

			builder.owner(owner)
				.section(section)
				.note(note)
				.page(page)
				.timestamp(timestamp)
				.coord(x, fx, y, fy)
				.force(force)
				.dotType(type)
				.color(color);
			return builder.Build();
		}

		private bool isF121MG(string macAddress)
		{
			const string MG_F121_MAC_START = "9C:7B:D2:22:00:00";
			const string MG_F121_MAC_END = "9C:7B:D2:22:18:06";
			ulong address = Convert.ToUInt64(macAddress.Replace(":", ""), 16);
			ulong mgStart = Convert.ToUInt64(MG_F121_MAC_START.Replace(":", ""), 16);
			ulong mgEnd = Convert.ToUInt64(MG_F121_MAC_END.Replace(":", ""), 16);

			if (address >= mgStart && address <= mgEnd)
				return true;
			else
				return false;
		}


        #endregion

        #region Pen Profile

        public bool IsSupportPenProfile()
        {
            string[] temp = ProtocolVersion.Split('.');
            float ver = 0f;
            try
            {
                ver = FloatConverter.ToSingle(temp[0] + "." + temp[1]);
            }
            catch (Exception e)
            {
                Debug.WriteLine(e.StackTrace);
            }
            if (ver >= PEN_PROFILE_SUPPORT_PROTOCOL_VERSION)
                return true;
            else
                return false;
        }

        /// <summary>
        /// Request to create profile
        /// </summary>
        /// <param name="profileName">Name of the profile to be created</param>
        /// <param name="password">Password of profile</param>
        //public void CreateProfile(string profileName, string password)
        public void CreateProfile(string profileName, byte[] password)
        {
            if (IsSupportPenProfile())
            {
                if (string.IsNullOrEmpty(profileName))
                    throw new ArgumentNullException("profileName");

                if (password == null)
                    throw new ArgumentNullException("password");

                byte[] profileNameBytes = Encoding.UTF8.GetBytes(profileName);
                //byte[] passwordBytes = Encoding.UTF8.GetBytes(password);
                if (profileNameBytes.Length > PenProfile.LIMIT_BYTE_LENGTH_PROFILE_NAME)
                    throw new ArgumentOutOfRangeException("profileName", $"profileName byte length must be {PenProfile.LIMIT_BYTE_LENGTH_PROFILE_NAME} or less");
                else if (password.Length != PenProfile.LIMIT_BYTE_LENGTH_PASSWORD)
                    throw new ArgumentOutOfRangeException("password", $"password byte length must be {PenProfile.LIMIT_BYTE_LENGTH_PASSWORD}");

                ReqCreateProfile(profileNameBytes, password);
            }
            else
                throw new NotSupportedException($"CreateProfile is not supported at this pen firmware version");

        }

        /// <summary>
        /// Request to delete profile
        /// </summary>
        /// <param name="profileName">Name of the profile to be deleted</param>
        /// <param name="password">password of profile</param>
        public void DeleteProfile(string profileName, byte[] password)
        {
            if (IsSupportPenProfile())
            {
                if (string.IsNullOrEmpty(profileName))
                    throw new ArgumentNullException("profileName");
                if (password == null)
                    throw new ArgumentNullException("password");

                byte[] profileNameBytes = Encoding.UTF8.GetBytes(profileName);
                //byte[] passwordBytes = Encoding.UTF8.GetBytes(password);
                if (profileNameBytes.Length > PenProfile.LIMIT_BYTE_LENGTH_PROFILE_NAME)
                    throw new ArgumentOutOfRangeException("profileName", $"profileName byte length must be {PenProfile.LIMIT_BYTE_LENGTH_PROFILE_NAME} or less");
                else if (password.Length != PenProfile.LIMIT_BYTE_LENGTH_PASSWORD)
                    throw new ArgumentOutOfRangeException("password", $"password byte length must be {PenProfile.LIMIT_BYTE_LENGTH_PASSWORD}");

                ReqDeleteProfile(profileNameBytes, password);
            }
            else
                throw new NotSupportedException($"CreateProfile is not supported at this pen firmware version");
        }

        /// <summary>
        /// Request information of the profile
        /// </summary>
        /// <param name="profileName">profile's name</param>
        public void GetProfileInfo(string profileName)
        {
            if (IsSupportPenProfile())
            {
                if (string.IsNullOrEmpty(profileName))
                    throw new ArgumentNullException("profileName");

                byte[] profileNameBytes = Encoding.UTF8.GetBytes(profileName);
                if (profileNameBytes.Length > PenProfile.LIMIT_BYTE_LENGTH_PROFILE_NAME)
                    throw new ArgumentOutOfRangeException("profileName", $"profileName byte length must be {PenProfile.LIMIT_BYTE_LENGTH_PROFILE_NAME} or less");

                ReqProfileInfo(profileNameBytes);
            }
            else
                throw new NotSupportedException($"CreateProfile is not supported at this pen firmware version");
        }

        /// <summary>
        /// Request to get data from profile
        /// </summary>
        /// <param name="profileName">profile name</param>
        /// <param name="keys">key array</param>
        public void ReadProfileValues(string profileName, string[] keys)
        {
            if (IsSupportPenProfile())
            {
                if (string.IsNullOrEmpty(profileName))
                    throw new ArgumentNullException("profileName");
                if (keys == null)
                    throw new ArgumentNullException("keys");

                byte[] profileNameBytes = Encoding.UTF8.GetBytes(profileName);
                if (profileNameBytes.Length > PenProfile.LIMIT_BYTE_LENGTH_PROFILE_NAME)
                    throw new ArgumentOutOfRangeException("profileName", $"profileName byte length must be {PenProfile.LIMIT_BYTE_LENGTH_PROFILE_NAME} or less");

                byte[][] keysBytes = new byte[keys.Length][];
                for (int i = 0; i < keys.Length; ++i)
                {
                    keysBytes[i] = Encoding.UTF8.GetBytes(keys[i]);
                    if (keysBytes[i].Length > PenProfile.LIMIT_BYTE_LENGTH_KEY)
                        throw new ArgumentOutOfRangeException("keys", $"key byte length must be {PenProfile.LIMIT_BYTE_LENGTH_KEY} or less");
                }

                ReqReadProfileValue(profileNameBytes, keysBytes);
            }
            else
                throw new NotSupportedException($"CreateProfile is not supported at this pen firmware version");
        }

        /// <summary>
        /// Request to write data
        /// </summary>
        /// <param name="profileName">profile name</param>
        /// <param name="password">password</param>
        /// <param name="keys">key array</param>
        /// <param name="data">data</param>
        public void WriteProfileValues(string profileName, byte[] password, string[] keys, byte[][] data)
        {
            if (IsSupportPenProfile())
            {
                if (string.IsNullOrEmpty(profileName))
                    throw new ArgumentNullException("profileName");
                if (password == null)
                    throw new ArgumentNullException("password");
                if (keys == null)
                    throw new ArgumentNullException("keys");
                if (data == null)
                    throw new ArgumentNullException("data");
                if (keys.Length != data.Length)
                    throw new ArgumentOutOfRangeException("keys, data", "The number of keys and data does not match");

                byte[] profileNameBytes = Encoding.UTF8.GetBytes(profileName);
                //byte[] passwordBytes = Encoding.UTF8.GetBytes(password);
                if (profileNameBytes.Length > PenProfile.LIMIT_BYTE_LENGTH_PROFILE_NAME)
                    throw new ArgumentOutOfRangeException("profileName", $"profileName byte length must be {PenProfile.LIMIT_BYTE_LENGTH_PROFILE_NAME} or less");
                else if (password.Length != PenProfile.LIMIT_BYTE_LENGTH_PASSWORD)
                    throw new ArgumentOutOfRangeException("password", $"password byte length must be {PenProfile.LIMIT_BYTE_LENGTH_PASSWORD}");

                byte[][] keysBytes = new byte[keys.Length][];
                for (int i = 0; i < keys.Length; ++i)
                {
                    keysBytes[i] = Encoding.UTF8.GetBytes(keys[i]);
                    if (keysBytes[i].Length > PenProfile.LIMIT_BYTE_LENGTH_KEY)
                        throw new ArgumentOutOfRangeException("keys", $"key byte length must be {PenProfile.LIMIT_BYTE_LENGTH_KEY} or less");
                }

                ReqWriteProfileValue(profileNameBytes, password, keysBytes, data);
            }
            else
                throw new NotSupportedException($"CreateProfile is not supported at this pen firmware version");
        }

        /// <summary>
        /// Request to delete data
        /// </summary>
        /// <param name="profileName">profile name</param>
        /// <param name="password">password</param>
        /// <param name="keys">key array</param>
        public void DeleteProfileValues(string profileName, byte[] password, string[] keys)
        {
            if (IsSupportPenProfile())
            {
                if (string.IsNullOrEmpty(profileName))
                    throw new ArgumentNullException("profileName");
                if (password == null)
                    throw new ArgumentNullException("password");
                if (keys == null)
                    throw new ArgumentNullException("keys");

                byte[] profileNameBytes = Encoding.UTF8.GetBytes(profileName);
                //byte[] passwordBytes = Encoding.UTF8.GetBytes(password);
                if (profileNameBytes.Length > PenProfile.LIMIT_BYTE_LENGTH_PROFILE_NAME)
                    throw new ArgumentOutOfRangeException("profileName", $"profileName byte length must be {PenProfile.LIMIT_BYTE_LENGTH_PROFILE_NAME} or less");
                else if (password.Length != PenProfile.LIMIT_BYTE_LENGTH_PASSWORD)
                    throw new ArgumentOutOfRangeException("password", $"password byte length must be {PenProfile.LIMIT_BYTE_LENGTH_PASSWORD}");

                byte[][] keysBytes = new byte[keys.Length][];
                for (int i = 0; i < keys.Length; ++i)
                {
                    keysBytes[i] = Encoding.UTF8.GetBytes(keys[i]);
                    if (keysBytes[i].Length > PenProfile.LIMIT_BYTE_LENGTH_KEY)
                        throw new ArgumentOutOfRangeException("keys", $"key byte length must be {PenProfile.LIMIT_BYTE_LENGTH_KEY} or less");
                }

                ReqDeleteProfileValue(profileNameBytes, password, keysBytes);
            }
            else
                throw new NotSupportedException($"CreateProfile is not supported at this pen firmware version");
        }

        private bool ReqCreateProfile(byte[] profileName, byte[] password)
        {
            ByteUtil bf = new ByteUtil(Escape);
            bf.Put(Const.PK_STX, false)
                .Put((byte)Cmd.PEN_PROFILE_REQUEST) // command
                .PutShort((short)(PenProfile.LIMIT_BYTE_LENGTH_PROFILE_NAME + 1 + PenProfile.LIMIT_BYTE_LENGTH_PASSWORD + 2 + 2))        // length
                .Put(profileName, PenProfile.LIMIT_BYTE_LENGTH_PROFILE_NAME)                // profile file name
                .Put(PenProfile.PROFILE_CREATE)     // type
                .Put(password, PenProfile.LIMIT_BYTE_LENGTH_PASSWORD)                   // password
                .PutShort(32)                       // section 크기 -> 32인 이유? 우선 android따라감. 확인필요
                .PutShort(32)                        // sector 개수(2^N 현재는 고정 2^8)
                .Put(Const.PK_ETX, false);

            return Send(bf);
        }

        private bool ReqDeleteProfile(byte[] profileName, byte[] password)
        {
            ByteUtil bf = new ByteUtil(Escape);
            bf.Put(Const.PK_STX, false)
                .Put((byte)Cmd.PEN_PROFILE_REQUEST) // command
                .PutShort((short)(PenProfile.LIMIT_BYTE_LENGTH_PROFILE_NAME + 1 + PenProfile.LIMIT_BYTE_LENGTH_PASSWORD))                // length
                .Put(profileName, PenProfile.LIMIT_BYTE_LENGTH_PROFILE_NAME)                // profile file name
                .Put(PenProfile.PROFILE_DELETE)     // type
                .Put(password, PenProfile.LIMIT_BYTE_LENGTH_PASSWORD)                   // password
                .Put(Const.PK_ETX, false);

            return Send(bf);
        }

        private bool ReqProfileInfo(byte[] profileName)
        {
            ByteUtil bf = new ByteUtil(Escape);
            bf.Put(Const.PK_STX, false)
                .Put((byte)Cmd.PEN_PROFILE_REQUEST) // command
                .PutShort((short)(PenProfile.LIMIT_BYTE_LENGTH_PROFILE_NAME + 1))                    // length
                .Put(profileName, PenProfile.LIMIT_BYTE_LENGTH_PROFILE_NAME)           // profile file name
                .Put(PenProfile.PROFILE_INFO)       // type
                .Put(Const.PK_ETX, false);

            return Send(bf);
        }

        private bool ReqWriteProfileValue(byte[] profileName, byte[] password, byte[][] keys, byte[][] data)
        {
            int dataLength = 0;
            int dataCount = data.Length;
            for (int i = 0; i < dataCount; ++i)
            {
                dataLength += PenProfile.LIMIT_BYTE_LENGTH_KEY;               // key
                dataLength += 2;                // data length
                dataLength += data[i].Length;   // data 
            }

            ByteUtil bf = new ByteUtil(Escape);
            bf.Put(Const.PK_STX, false)
                .Put((byte)Cmd.PEN_PROFILE_REQUEST)             // command
                .PutShort((short)(PenProfile.LIMIT_BYTE_LENGTH_PROFILE_NAME + 1 + PenProfile.LIMIT_BYTE_LENGTH_PASSWORD + 1 + dataLength))  // length
                .Put(profileName, PenProfile.LIMIT_BYTE_LENGTH_PROFILE_NAME)                       // profile file name
                .Put(PenProfile.PROFILE_WRITE_VALUE)            // type
                .Put(password, PenProfile.LIMIT_BYTE_LENGTH_PASSWORD)                          // password
                .Put((byte)dataCount);                          // count

            for (int i = 0; i < dataCount; ++i)
            {
                bf.Put(keys[i], PenProfile.LIMIT_BYTE_LENGTH_KEY)
                    .PutShort((short)data[i].Length)
                    .Put(data[i]);
            }

            bf.Put(Const.PK_ETX, false);

            return Send(bf);
        }

        private bool ReqReadProfileValue(byte[] profileName, byte[][] keys)
        {
            ByteUtil bf = new ByteUtil(Escape);
            bf.Put(Const.PK_STX, false)
                .Put((byte)Cmd.PEN_PROFILE_REQUEST)                 // command
                .PutShort((short)(PenProfile.LIMIT_BYTE_LENGTH_PROFILE_NAME + 1 + 1 + PenProfile.LIMIT_BYTE_LENGTH_KEY * keys.Length))    // Length
                .Put(profileName, PenProfile.LIMIT_BYTE_LENGTH_PROFILE_NAME)                           // profile file name
                .Put(PenProfile.PROFILE_READ_VALUE)                 // Type
                .Put((byte)keys.Length);                            // Key Count

            for (int i = 0; i < keys.Length; ++i)
            {
                bf.Put(keys[i], PenProfile.LIMIT_BYTE_LENGTH_KEY);
            }

            bf.Put(Const.PK_ETX, false);

            return Send(bf);
        }

        private bool ReqDeleteProfileValue(byte[] profileName, byte[] password, byte[][] keys)
        {
            ByteUtil bf = new ByteUtil(Escape);
            bf.Put(Const.PK_STX, false)
                .Put((byte)Cmd.PEN_PROFILE_REQUEST)                     // command
                .PutShort((short)(PenProfile.LIMIT_BYTE_LENGTH_PROFILE_NAME + 1 + PenProfile.LIMIT_BYTE_LENGTH_PASSWORD + 1 + PenProfile.LIMIT_BYTE_LENGTH_KEY * keys.Length))    // Length
                .Put(profileName, PenProfile.LIMIT_BYTE_LENGTH_PROFILE_NAME)                               // profile file name
                .Put(PenProfile.PROFILE_DELETE_VALUE)                   // Type
                .Put(password, PenProfile.LIMIT_BYTE_LENGTH_PASSWORD)                                  // password
                .Put((byte)keys.Length);                                // key count

            for (int i = 0; i < keys.Length; ++i)
            {
                bf.Put(keys[i], PenProfile.LIMIT_BYTE_LENGTH_KEY);
            }

            bf.Put(Const.PK_ETX, false);

            return Send(bf);
        }

        #endregion

        #region Encryption

        /// <summary>
        /// Install (update) the certificate on the pen. 
        /// (When the certificate is installed, the pen is set to the authentication mode and you cannot receive pen data without the private key.)
        /// </summary>
        /// <param name="certificatePath">Path to the certificate file</param>
        /// <returns>True if the request succeeds false if it fails</returns>
        public bool ReqUpdateCertificate(string certificatePath)
        {
            var certificate = System.IO.File.ReadAllBytes(certificatePath);

            if (certificate == null || certificate.Length <= 0)
                return false;

            ByteUtil bf = new ByteUtil(Escape);
            bf.Put(Const.PK_STX, false)
              .Put((byte)Cmd.ENCRYPTION_CERT_UPDATE_REQUEST)
              .PutShort((short)certificate.Length)
              .Put(certificate)
              .Put(Const.PK_ETX, false);

            return Send(bf);
        }

        /// <summary>
        /// Request to remove the certificate currently installed on the pen.
        /// </summary>
        /// <param name="serialNumber">Serial number specified or issued with certificate</param>
        /// <returns>True if the request succeeds false if it fails</returns>
        public bool ReqDeleteCertificate(string serialNumber)
        {
            if (string.IsNullOrEmpty(serialNumber))
                return false;
            try
            {
                byte[] scodeByte = BigInteger.Parse(serialNumber).ToByteArray();
                Array.Reverse(scodeByte);

                ByteUtil bf = new ByteUtil(Escape);
                bf.Put(Const.PK_STX, false)
                  .Put((byte)Cmd.ENCRYPTION_CERT_DELETE_REQUEST)
                  .PutShort(17)
                  .Put((byte)scodeByte.Length)
                  .Put(scodeByte, 16)
                  .Put(Const.PK_ETX, false);

                return Send(bf);
            }
            catch
            {
                return false;
            }
        }

        private bool ReqEncryptionKey()
        {
            ByteUtil bf = new ByteUtil(Escape);
            bf.Put(Const.PK_STX, false)
              .Put((byte)Cmd.ENCRYPTION_KEY_REQUEST)
              .PutShort(8)
              .PutNull(8)
              .Put(Const.PK_ETX, false);

            return Send(bf);
        }

        /// <summary>
        /// Set the private key.
        /// (It must be set before the onPenAuthenticated callback is called or when the onPrivateKeyRequest callback is called, i.e. before the authentication process.)
        /// </summary>
        /// <param name="privateKey">PrivateKey instance</param>
        /// <returns>True if set succeeded false if failed</returns>
        public bool SetPrivateKey(PrivateKey privateKey)
        {
            if (Alive)
            {
                if (!isPenAuthenticated)
                {
                    this.rsaKeys = privateKey;
                    ReqPenStatus();
                    return true;
                }
            }
            else
            {
                this.rsaKeys = privateKey;
                return true;
            }

            return false;
        }
        #endregion
    }
}
