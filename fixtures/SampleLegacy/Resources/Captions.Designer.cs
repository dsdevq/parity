namespace SampleLegacy.Resources
{
    // shaped like the class PublicResXFileCodeGenerator writes for Captions.resx
    public class Captions
    {
        private static System.Resources.ResourceManager resourceMan;

        public static System.Resources.ResourceManager ResourceManager
        {
            get
            {
                if (resourceMan == null) resourceMan = new System.Resources.ResourceManager("SampleLegacy.Resources.Captions", typeof(Captions).Assembly);
                return resourceMan;
            }
        }

        public static string colEffectiveDate { get { return ResourceManager.GetString("colEffectiveDate"); } }
        public static string colReason { get { return ResourceManager.GetString("colReason"); } }
        public static string lsHistoryTitle { get { return ResourceManager.GetString("lsHistoryTitle"); } }
        public static string lsReasons { get { return ResourceManager.GetString("lsReasons"); } }
    }
}
