using System;
using System.Runtime.InteropServices;

namespace NPPAI.Npp
{
    // ---------------------------------------------------------------------
    // Notepad++ plugin API structures (must match NPP's exact layout on x86).
    // ---------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    public struct NPP_DATA
    {
        public IntPtr nppHandle;     // HWND of the Notepad++ main window
        public IntPtr sciHandle;     // HWND of the Scintilla editor
        public IntPtr pNppFunctionList; // NPP_FUNCTION_LIST*
    }

    public enum NppVersion
    {
        V640 = 640,
        V641 = 641,
        V642 = 642,
        V643 = 643,
        V644 = 644,
        V650 = 650,
        V660 = 660,
        V661 = 661,
        V662 = 662,
        V663 = 663,
        V664 = 664,
        V670 = 670,
        V671 = 671,
        V672 = 672,
        V673 = 673,
        V674 = 674,
        V675 = 675,
        V676 = 676,
        V677 = 677,
        V678 = 678,
        V679 = 679,
        V680 = 680,
        V681 = 681,
        V682 = 682,
        V683 = 683,
        V684 = 684,
        V685 = 685,
        V686 = 686,
        V687 = 687,
        V688 = 688,
        V689 = 689,
        V690 = 690,
        V700 = 700,
        V710 = 710,
        V711 = 711,
        V712 = 712,
        V713 = 713,
        V714 = 714,
        V715 = 715,
        V716 = 716,
        V717 = 717,
        V718 = 718,
        V719 = 719,
        V720 = 720,
        V721 = 721,
        V730 = 730,
        V740 = 740,
        V741 = 741,
        V742 = 742,
        V743 = 743,
        V744 = 744,
        V745 = 745,
        V746 = 746,
        V750 = 750,
        V751 = 751,
        V752 = 752,
        V753 = 753,
        V754 = 754,
        V755 = 755,
        V756 = 756,
        V757 = 757,
        V758 = 758,
        V759 = 759,
        V760 = 760,
        V761 = 761,
        V762 = 762,
        V763 = 763,
        V764 = 764,
        V765 = 765,
        V766 = 766,
        V767 = 767,
        V768 = 768,
        V769 = 769,
        V770 = 770,
        V771 = 771,
        V772 = 772,
        V773 = 773,
        V774 = 774,
        V775 = 775,
        V776 = 776,
        V777 = 777,
        V778 = 778,
        V779 = 779,
        V780 = 780,
        V781 = 781,
        V782 = 782,
        V783 = 783,
        V784 = 784,
        V785 = 785,
        V786 = 786,
        V787 = 787,
        V788 = 788,
        V789 = 789,
        V790 = 790,
        V791 = 791,
        V792 = 792,
        V793 = 793,
        V794 = 794,
        V795 = 795,
        V796 = 796,
        V797 = 797,
        V798 = 798,
        V799 = 799,
        V800 = 800,
        V810 = 810,
        V820 = 820,
        V830 = 830,
        V840 = 840,
        V841 = 841,
        V842 = 842,
        V843 = 843,
        V844 = 844,
        V845 = 845,
        V846 = 846,
        V847 = 847,
        V848 = 848,
        V849 = 849,
        V850 = 850,
        V851 = 851,
        V852 = 852,
        V853 = 853,
        V854 = 854,
        V855 = 855,
        V856 = 856,
        V857 = 857,
        V858 = 858,
        V859 = 859,
        V860 = 860,
        V861 = 861,
        V862 = 862,
        V863 = 863,
        V864 = 864,
        V865 = 865,
        V866 = 866,
        V867 = 867,
        V868 = 868,
        V869 = 869,
        V870 = 870,
        V871 = 871,
        V872 = 872,
        V873 = 873,
        V874 = 874,
        V875 = 875,
        V876 = 876,
        V877 = 877,
        V878 = 878,
        V879 = 879,
        V880 = 880,
        V881 = 881,
        V882 = 882,
        V883 = 883,
        V884 = 884,
        V885 = 885,
        V886 = 886,
        V887 = 887,
        V888 = 888,
        V889 = 889,
        V890 = 890,
        V891 = 891,
        V892 = 892,
        V893 = 893,
        V894 = 894,
        V895 = 895,
        V896 = 896,
        V897 = 897,
        V898 = 898,
        V899 = 899,
        V900 = 900,
        V901 = 901,
        V902 = 902,
        V903 = 903,
        V904 = 904,
        V905 = 905,
        V906 = 906,
        V907 = 907,
        V908 = 908,
        V909 = 909,
        V910 = 910,
        V911 = 911,
        V912 = 912,
        V913 = 913,
        V914 = 914,
        V915 = 915,
        V916 = 916,
        V917 = 917,
        V918 = 918,
        V919 = 919,
        V920 = 920,
        V921 = 921,
        V922 = 922,
        V923 = 923,
        V924 = 924,
        V925 = 925,
        V926 = 926,
        V927 = 927,
        V928 = 928,
        V929 = 929,
        V930 = 930,
        V931 = 931,
        V932 = 932,
        V933 = 933,
        V934 = 934,
        V935 = 935,
        V936 = 936,
        V937 = 937,
        V938 = 938,
        V939 = 939,
        V940 = 940,
        V941 = 941,
        V942 = 942,
        V943 = 943,
        V944 = 944,
        V945 = 945,
        V946 = 946,
        V947 = 947,
        V948 = 948,
        V949 = 949,
        V950 = 950,
        V951 = 951,
        V952 = 952,
        V953 = 953,
        V954 = 954,
        V955 = 955,
        V956 = 956,
        V957 = 957,
        V958 = 958,
        V959 = 959,
        V960 = 960,
        V961 = 961,
        V962 = 962,
        V963 = 963,
        V964 = 964,
        V965 = 965,
        V966 = 966,
        V967 = 967,
        V968 = 968,
        V969 = 969,
        V970 = 970,
        V971 = 971,
        V972 = 972,
        V973 = 973,
        V974 = 974,
        V975 = 975,
        V976 = 976,
        V977 = 977,
        V978 = 978,
        V979 = 979,
        V980 = 980,
        V981 = 981,
        V982 = 982,
        V983 = 983,
        V984 = 984,
        V985 = 985,
        V986 = 986,
        V987 = 987,
        V988 = 988,
        V989 = 989,
        V990 = 990,
        V991 = 991,
        V992 = 992,
        V993 = 993,
        V994 = 994,
        V995 = 995,
        V996 = 996,
        V997 = 997,
        V998 = 998,
        V999 = 999,
        V1000 = 1000,
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NPP_FUNCTION_LIST
    {
        public IntPtr SetScintillaNotifier; // void (*)(NPP_DATA*, SCNotification*)
        public IntPtr SetLocale;            // void (*)(NPP_DATA*, int)
        public IntPtr SetWinVersion;        // void (*)(NPP_DATA*, int)
        public IntPtr SetVersion;           // void (*)(NPP_DATA*, NppVersion)
    }

    // ---------------------------------------------------------------------
    // Scintilla notification (SCNotification). The trailing text buffer is
    // 8192 chars; we marshal it as LPStr so the struct size matches.
    // ---------------------------------------------------------------------
    [StructLayout(LayoutKind.Sequential)]
    public struct SCNotification
    {
        public int iMessage;   // == SCNotification (202)
        public int idFrom;     // == the Scintilla control's HWND (as int)
        public int iCode;      // SCN_* code
        public IntPtr wParam;
        public IntPtr lParam;
        [MarshalAs(UnmanagedType.LPStr)]
        public string text;
    }

    // SC_MOD (lParam of SCN_MODIFIED)
    [StructLayout(LayoutKind.Sequential)]
    public struct SCModified
    {
        public int modification;       // bitmask of SCI_CHANGE_* flags
        public int modifiedTextType;   // 0 = not a text change
        public IntPtr text;            // SC_TEXTCHANGED* when modifiedTextType != 0
    }

    // SC_TEXTCHANGED (pointed to by SCModified.text)
    [StructLayout(LayoutKind.Sequential)]
    public struct SC_TEXTCHANGED
    {
        public int position;
        public int line;
        public int modificationType;
        public int charsAdded;
        public int linesAdded;
        public int linePosition;
        public int lineLength;
    }

    // ---------------------------------------------------------------------
    // SCN menu (SCN_MENU) — passed to NPP_CMDMENU via p.
    // ---------------------------------------------------------------------
    [StructLayout(LayoutKind.Sequential)]
    public struct SCN_MENU
    {
        public int position;
        public int items;
        public IntPtr pMenu; // MENUITEMINFO*
    }
}
