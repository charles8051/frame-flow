namespace FrameFlow.Decoding.Tests;

/// <summary>
/// Two JPEGs of the same 256x144 gradient, written with Pillow at quality 90. One is baseline
/// (SOF0), one is progressive (SOF2). The CUDA MJPEG decoder accepts the first and refuses the
/// second at its first packet (#572).
/// </summary>
internal static class JpegFixtures
{
    public static byte[] Baseline => Convert.FromBase64String(BaselineBase64);

    public static byte[] Progressive => Convert.FromBase64String(ProgressiveBase64);

    private const string BaselineBase64 =
            "/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAMCAgMCAgMDAwMEAwMEBQgFBQQEBQoHBwYIDAoMDAsKCwsNDhIQDQ4RDgsLEBYQ" +
            "ERMUFRUVDA8XGBYUGBIUFRT/2wBDAQMEBAUEBQkFBQkUDQsNFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQU" +
            "FBQUFBQUFBQUFBQUFBT/wAARCACQAQADASIAAhEBAxEB/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAA" +
            "AgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6" +
            "Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXG" +
            "x8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREA" +
            "AgECBAQDBAcFBAQAAQJ3AAECAxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRomJygpKjU2Nzg5" +
            "OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPE" +
            "xcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq8vP09fb3+Pn6/9oADAMBAAIRAxEAPwD83FtvapktvatBbWpkta/px1T52lXM9Lb2" +
            "qdLb2q+lrU6WtYOqetSrmelt7VMlt7VoJbVOlrWDqnq0q5npbe1TJbe1aCWtTJa1g6p61KuUEtvapktvatBLWpktawdU9WlX" +
            "M9Lb2qdLb2q+lrU6Wtc7qnq0q5npbe1TJbe1aCWtTpa1hKqetSrmelt7VMlt7VoJa1MlrWEqp6tKuUEtvapktvatBLWpktaw" +
            "dU9WlXM9Lb2qdLb2rQS1qZLasJVT1qVcz0tvapktvatBLWp0tawlVPVpVzPS29qmS29q0EtqmS1rndU9WlXKCW3tUyW3tWgl" +
            "rUyWtYOqetSrmelt7VOlt7VoJa1MlrWDqnq0q5npbe1Tpbe1X0tanS1rB1T1aVcz0tvapktvatBLWpkta53VPWpVz5QS2qZL" +
            "atBbb2qZLb2r9TdU/wAn6VcoJbVMltWglt7VMlt7VhKqerSrmeltU6W1aCW3tUyW3tWEqp61KuZ6W1TJbVoJbe1Tpbe1YOqe" +
            "rSrmelt7VMltWglt7VOlt7Vg6p6tKuZ6W1TJbVoJbe1TJbe1YOqetSrmeltU6W1aCW3tUyW3tXO6p6tKuZ6W1TpbVfS29qnS" +
            "29qwdU9WlXM9Lb2qZLatBLb2qdLb2rCVU9alXM9Lapktq0EtvapktvasHVPVpVzPS2qdLatBLb2qZLb2rnlVPVpVzPS2qdLa" +
            "tBLb2qZLb2rCVU9alXM9Lapktq0Etvap0tvasJVT1aVcz0tqmS2rQS29qmS29qwlVPVpVzPS2qdLatBLb2qZLb2rB1T1qVcz" +
            "0tqnS29qvpbe1Tpbe1YOqerSrnyetr7VOlr7VfS19qnS19q/UnVP8n6Vcz0tfapktfatBLX2qZLX2rCVU9alXKCWvtUyWvtW" +
            "glr7VMlr7VhKqerSrmelr7VOlr7VoJa+1TJa+1YOqerSrmelr7VOlr7VfS19qnS19qwlVPWpVzPS19qmS19q0Etfapktfaud" +
            "1T1aVcoJa+1TJa+1aCWvtUyWvtWDqnrUq5npa+1Tpa+1aCWvtUyWvtWDqnq0q5npa+1Tpa+1X0tfap0tfasJVT1aVcz0tfap" +
            "ktfatBLX2qZLX2rB1T1qVcoJa+1TJa+1aCWvtUyWvtXO6p6tKuUEtfapktfatBLX2qZLX2rCVU9WlXM9LX2qdLX2q+lr7VOl" +
            "r7VhKqetSrmelr7VMlr7VoJa+1TJa+1YOqerSrlBLX2qZLX2rQS19qmS19qwlVPVpVyglr7VMlr7VoJa+1TJa+1c8qp61Kuf" +
            "KCW3tUyW3tWgtrUyWtfqcqp/k/Srmelt7VOlt7VfS1qdLWsJVT1aVcz0tvapktvatBLWp0tawdU9WlXM9Lb2qZLb2rQS1qZL" +
            "WsJVT1qVcoJbe1TJbe1aCWtTJa1hKqerSrmelt7VOlt7VfS2qdLWsHVPVpVzPS29qmS29q0EtanS1rnlVPWpVzPS29qmS29q" +
            "0EtamS1rB1T1aVcoJbe1TJbe1aCWtTJa1g6p61KuZ6W3tU6W3tWglrUyWtYOqerSrmelt7VOlt7VfS1qdLWueVU9WlXM9Lb2" +
            "qZLb2rQS2qZLasJVT1qVcoJbe1TJbe1aCWtTJa1g6p6tKuZ6W3tU6W3tWglrUyWtYSqnq0q5npbe1Tpbe1X0tanS1rCVU9al" +
            "XM9Lb2qZLb2rQS1qZLWud1T1aVc+UEtqmS2rQS29qmS29q/U3VP8n6VcoJbVMltWglt7VMlt7Vg6p6tKuZ6W1TpbVoJbe1TJ" +
            "be1YSqnrUq5npbe1TJbVoJbe1Tpbe1YOqerSrmeltUyW1aCW3tU6W3tXPKqerSrmeltUyW1aCW3tUyW3tWEqp61KuZ6W1Tpb" +
            "VoJbe1TJbe1YOqerSrmeltU6W1X0tvap0tvasHVPVpVzPS29qmS2rQS29qnS29qwlVPWpVzPS2qZLatBLb2qZLb2rndU9WlX" +
            "M9Lap0tq0EtvapktvasHVPWpVzPS2qdLatBLb2qZLb2rB1T1aVcz0tqmS2rQS29qnS29qwlVPVpVzPS2qZLatBLb2qZLb2rB" +
            "1T1qVcoJbVMlt7VoJbe1TJbe1c8qp6tKuZ6W1TpbVoJbe1TJbe1YSqnq0q58nra+1Tpa+1aC2vtUyWvtX6m6p/k/Srmelr7V" +
            "Mlr7VoJa+1Tpa+1YOqetSrmelr7VMlr7VoJa+1TJa+1YOqerSrlBLX2qZLX2rQS19qmS19qwdU9WlXM9LX2qdLX2q+lr7VOl" +
            "r7VhKqetSrmelr7VMlr7VoJa+1Tpa+1c8qp6tKuZ6WvtUyWvtWglr7VMlr7VhKqerSrlBLX2qZLX2rQS19qmS19qwdU9alXM" +
            "9LX2qdLX2q+lr7VOlr7VhKqerSrmelr7VOlr7VfS19qnS19qwlVPVpVzPS19qmS19q0Etfapktfaud1T1qVcoJa+1TJa+1aC" +
            "WvtUyWvtWDqnq0q5npa+1Tpa+1X0tfap0tfasHVPVpVzPS19qnS19qvpa+1Tpa+1YOqetSrmelr7VMlr7VoJa+1TJa+1c8qp" +
            "6tKuUEtfapktfatBLX2qZLX2rCVU9WlXPlBLb2qZLb2rQW1qZLWv1N1T/KClXM9Lb2qdLb2rQS2qZLWsJVT1aVcz0tvap0tv" +
            "ar6WtTpa1g6p6tKuZ6W3tUyW3tWglrUyWtYOqetSrlBLb2qZLb2rQS1qZLWud1T1aVcz0tvap0tvatBLWpktawdU9WlXM9Lb" +
            "2qdLb2q+ltU6WtYSqnrUq5npbe1TJbe1aCWtTJa1hKqerSrlBLb2qZLb2rQS1qZLWsJVT1aVcz0tvap0tvatBLWpkta53VPW" +
            "pVzPS29qnS29qvpa1OlrWEqp6tKuZ6W3tUyW3tWglrUyW1YSqnrUq5QS29qmS29q0EtamS1rB1T1aVcz0tvap0tvatBLWpkt" +
            "awdU9WlXM9Lb2qdLb2q+lrU6Wtc7qnrUq5npbe1TJbe1aCWtTpa1g6p6tKufJ621TJbVoJbe1Tpbe1fqbqn+T9KuZ6W1TJbV" +
            "oJbe1TJbe1YSqnrUq5npbVOltWglt7VMlt7VhKqerSrmelt7VOltV9Lb2qdLb2rCVU9WlXM9Lapktq0Etvap0tvaud1T1qVc" +
            "z0tqmS2rQS29qmS29qwdU9WlXM9Lap0tq0EtvapktvasHVPVpVzPS2qdLar6W3tU6W3tWDqnrUq5npbVMltWglt7VOlt7Vzy" +
            "qnq0q5npbVMlt7VoJbe1TJbe1YSqnq0q5npbVOltWglt7VMlt7Vg6p61KuZ6W1TpbVoJbe1TJbe1YSqnq0q5npbVMltWglt7" +
            "VOlt7VhKqerSrmeltUyW1aCW3tUyW3tWEqp61KuUEtqmS29q0Etvapktvaud1T1aVcz0tqnS2rQS29qmS29qwdU9WlXPk9LX" +
            "2qdLX2rQW19qmS19q/U3VP8AKClXM9LX2qZLX2rQS19qnS19qwdU9WlXM9LX2qZLX2rQS19qmS19qwlVPVpVyglr7VMlr7Vo" +
            "Ja+1TJa+1YSqnrUq5npa+1Tpa+1aCWvtUyWvtXPKqerSrmelr7VOlr7VfS19qnS19qwdU9WlXM9LX2qZLX2rQS19qmS19qwd" +
            "U9alXKCWvtUyWvtWglr7VMlr7Vg6p6tKuZ6WvtU6WvtWglr7VMlr7Vg6p6tKuZ6WvtU6WvtV9LX2qdLX2rndU9alXM9LX2qZ" +
            "LX2rQS19qmS19qwlVPVpVyglr7VMlr7VoJa+1TJa+1YOqerSrmelr7VOlr7VoJa+1TJa+1YSqnrUq5npa+1Tpa+1X0tfap0t" +
            "fasJVT1aVcz0tfapktfatBLX2qZLX2rnlVPWpVyglr7VMlr7VoJa+1TJa+1YOqerSrn/2Q==";

    private const string ProgressiveBase64 =
            "/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAMCAgMCAgMDAwMEAwMEBQgFBQQEBQoHBwYIDAoMDAsKCwsNDhIQDQ4RDgsLEBYQ" +
            "ERMUFRUVDA8XGBYUGBIUFRT/2wBDAQMEBAUEBQkFBQkUDQsNFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQU" +
            "FBQUFBQUFBQUFBQUFBT/wgARCACQAQADASIAAhEBAxEB/8QAGAABAQEBAQAAAAAAAAAAAAAABAIDBwj/xAAaAQEBAQEBAQEA" +
            "AAAAAAAAAAAEAwIGCAUH/9oADAMBAAIQAxAAAAHzbaL/AE75x9N9JrPaLwo9oua8LReFntFzUe0aTWe0XhZ7Rc1H0ReFntFz" +
            "We0XNWFovCz2i5rPpvphR7Rc18ntF9T5PwtF4We0XNZ7RphR7Rc1ntF4WfRFzUe0XNZ7RphZ7Rc1HtFzWfRF4We0XNR7ReFn" +
            "0Rc1ntF4VyfTfTqfJ57Rc1ntF4WfRFzUe0XhZ7Rc14Wi5rPaLwo+m+k1ntF4We0XNWFouaz2i8LPaLmrC0XhZ7Rc18otF9T5" +
            "PPaLwo9o0ms9ovCz2i5qPpvphZ7Rc1ntFzVhaLws9ouaz6b6TUe0XhZ7Rc1n0ReFHtFzWe0XNfKLRfVeTz2i5qPoi8LPaLms" +
            "9o0mo9ovCz2i5rPpvphR7Rc1ntFzWfRF4We0XNR7RphZ7Rc1ntFzUfRF4Xye0X1Pk89o0ws9ouaj2i8LPpvpNZ7Rc1HtF4Xh" +
            "aLms9ovCj6b6TWe0XNZ7ReFH030ms9ovCz2i5qwtFzXye0X1Xk8+iLms9ovCj2i5rwtFzWe0XhR9N9JrPaLws9ouaj6Iuaz2" +
            "i8LPaLmvC0XhR7Rc1n030ms9ovCuT2jTqfJ57ReFntFzWfTfTCj2i5rPaLms+iLwo9ouaz2jSaz2i8KPaLms+iLws9ouaj2i" +
            "8LwtFzWe0XNXJ9EX1XlA9ouaj2i8LwtFzWe0XNR9N9MLPaLms9ovCj6Iuaz2i5rPaLwrC0XNZ7ReFn030mo9ouaz2i8L/8QA" +
            "FRABAQAAAAAAAAAAAAAAAAAAABL/2gAIAQEAAQUClKUpSlKUpSlKUpSlKUpSlKUpSlKUpSlKUpSlKUpSlKUpSlKUpSlKUpSl" +
            "KUpSlKUpSlKUpSlKUpSlKUpSlKUpSlKUpSlKUpSlKUpSlKUpSlKUpSlKUpSlKUpSlKUpSlKUpSlKUpSlKUpSlKUpSlKUpSlK" +
            "UpSlKUpSlKUpSlKUpSlKUpSlKUpSlKUpSlKUpSlKUpSlKUpSlKUpSlKUpSlKUpSlKUpSlKUpSlKUpSlKUpSlKUpSlKUpSlKU" +
            "pSlKUpSlKUpSlKUpSlKUpSlKUpSlKUpSlKUpSlKUpSlKUpSlKUpSlKUpSlKUpSlKUpSlKUpSlKUpSlKUpSlKUpSlKUpSlKUp" +
            "SlKUpSlKUpSlKUpSlKUpSlKUpSlKUpSlKUpSlKUpSlKUpSlKUpSlKUpSlKUpSlKUpSlKUpSlKUpSlKUpSlKUpSlKUpSlKUpS" +
            "lKUpSlKUpSlKUpSlKUpSlKUpSlKUpSlKUpSlKUpS/8QAFREBAQAAAAAAAAAAAAAAAAAAAgD/2gAIAQMBAT8BLi4uLi4uLi4u" +
            "Li4uLi4uLi4uLi4uLi4uLi4uLi4uLi4uLi4uLi4uLi4uLi4uLi4uLi4uLi4uLi4uLi4uLi4uLi4uLi4uLi4uLi4uLi4uLi4u" +
            "Li4uLi4uLi4uLi4uLi4uLi4uLi4uLi4uLi4uLi4uLi4uLi4uLi4uLi4uLi4uLi4uLi4uLi4uLi4uLi4uLi4u/8QAFxEBAQEB" +
            "AAAAAAAAAAAAAAAAABIBAv/aAAgBAgEBPwGlKUpTem9Kb03pSlKUpvTelKUpTelN6b03pvSlKb03pTelKU3pSm9N6U3pvTem" +
            "9Kb03pTelKU3pvSm9N6UpTelN6b0pTelKU3pTem9KUpTem9N6U3pvSlKU3pvSm9KUpTem9N6U3pvSlKUpvTem9KUpTem9Kb0" +
            "3pvSlKU3pvTelKUpTelN6b03pT//xAAUEAEAAAAAAAAAAAAAAAAAAACQ/9oACAEBAAY/AhA//8QAFRABAQAAAAAAAAAAAAAA" +
            "AAAAAAH/2gAIAQEAAT8hhCEIQhCEIQhCEIQhCEIQhCEIQhCMYxhGMYxjGMYxjGMIxhGMYxjGMYQhCEIQhCEIQhCEIQhCEIQh" +
            "CEIQhCEIQhCEIQhCEIQhCEIQhCEIQhCMYxjGMYxjGMYxjGMYxjGMYxjGMYxjGMYxjGMYxjGMYxjGMYxjGMYxhCEIQhCEIQhC" +
            "EIQhCEIQhCEIQhCMYxjGMYxhGMYxjGMYxhCMYxjGMYQhCEIQhCEIQhCEIQhCEIQhCEIQhCEIQhCEIQhCEIQhCEIQhCEIQhCM" +
            "YxjGMYxjGMYxjGMYxjGMYxjGMYxjGMYxjGMYxjGMYxjGMYxjGMYxhCEIQhCEIQhCEIQhCEIQhCEIQhCMYRjGMYxjGEYxjGMY" +
            "xjCMYxjGMYQhCEIQhCEIQhCEIQhCEIQhCEIQhCEIQhCEIQhCEIQhCEIQhCEIQhCMYxjGMYxjGMYxjGMYxjGMYxjGMYxjGMYx" +
            "jGMYxjGMYxjGMYxjGMYx/9oADAMBAAIAAwAAABDyoX8BXsD+Eb8F6kH8B7kbyFbgN6AfyJWkPyNXwJ2oD0JT8DwJXoD+gT8F" +
            "6sH6Ff4D+EbsF6kf6B/gL+B7gPyofwJWoL2pT0L2JX8J2sD+NT4F6oXsF6kX6FfoF+Af0B+AfwJ+gL2Jb0JysPz/xAAUEQEA" +
            "AAAAAAAAAAAAAAAAAABw/9oACAEDAQE/EBD/AP8A/wD/AP8A/wD/AP8A/wD/AP8A/wD/AP8A/wD/AP8A/wD/AP8A/wD/AP8A" +
            "/wD/AP8A/wD/AP8A/wD/AP8A/wD/AP8A/wD/xAAXEQEBAQEAAAAAAAAAAAAAAAABABAg/9oACAECAQE/EGta1rtXata1rXat" +
            "a1rleaq1rtXK1rla7V5qrtXK1rtXata5XatcrWuV2rWteKrtWta7Vyta14qu1a1rXiq1rXavFVrWvFVrWtcrxVb/AP/EABgQ" +
            "AQEBAQEAAAAAAAAAAAAAAGEAMFAQ/9oACAEBAAE/EBjjjjjjjjjjjjjjjjjjjjjjjjjjjjjjj5JV1VVVV11VVf8A/nh//nv/" +
            "AP8A/wD5wxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxwRRRRRRRRRRRRRRRRRRRRRRRRRRRRRRRRRRRRRRRRRRRRRRRRRRRRRRR" +
            "RRRRRRRRRxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx8cqqq6qqq+qqv/zy/wD/AM9f/wD/APP3zjjjjjjjjjjjjjjjjjjjjjjj" +
            "jjjjjjjjgiiiiiiiiiiiiiiiiiiiiiiiiiiiiiiigiiiiiiiiiiiiiiiiiiiiiiiiiiiiiiijjjjjjjjjjjjjjjjjjjjjjjj" +
            "jjjjjjjj45dVVXVVVdVVP/nr/wD/AP55f/8A5++ccccccccccccccccccccccccccccccccUUUUUUUUUUUUUUUUUUUUUUUUU" +
            "UUUUUUUEUUUUUUUUUUUUUUUUUUUUUUUUUUUUUUV//9k=";
}
